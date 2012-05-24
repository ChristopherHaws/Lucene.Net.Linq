﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Lucene.Net.Index;
using Lucene.Net.Linq.Expressions;
using Lucene.Net.Linq.Mapping;
using Lucene.Net.Linq.Search;
using Lucene.Net.Linq.Util;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;

namespace Lucene.Net.Linq.Translation.TreeVisitors
{
    internal class QueryBuildingExpressionTreeVisitor : LuceneExpressionTreeVisitor
    {
        private readonly Context context;
        private readonly IFieldMappingInfoProvider fieldMappingInfoProvider;
        private readonly Stack<Query> queries = new Stack<Query>();

        internal QueryBuildingExpressionTreeVisitor(Context context, IFieldMappingInfoProvider fieldMappingInfoProvider)
        {
            this.context = context;
            this.fieldMappingInfoProvider = fieldMappingInfoProvider;
        }

        public Query Query
        {
            get
            {
                if (queries.Count == 0) return new MatchAllDocsQuery();
                var query = queries.Peek();
                if (query is BooleanQuery)
                {
                    var booleanQuery = (BooleanQuery)query.Clone();
                    if (booleanQuery.GetClauses().All(c => c.GetOccur() == BooleanClause.Occur.MUST_NOT))
                    {
                        booleanQuery.Add(new MatchAllDocsQuery(), BooleanClause.Occur.SHOULD);
                        return booleanQuery;
                    }
                }
                return query;
            }
        }

        public Query Parse(string fieldName, string pattern)
        {
            var queryParser = new QueryParser(context.Version, fieldName, context.Analyzer);
            queryParser.SetLowercaseExpandedTerms(false);
            queryParser.SetAllowLeadingWildcard(true);
            return queryParser.Parse(pattern);
        }

        protected override Expression VisitBinaryExpression(BinaryExpression expression)
        {
            switch (expression.NodeType)
            {
                case ExpressionType.AndAlso:
                case ExpressionType.OrElse:
                    return MakeBooleanQuery(expression);
                default:
                    throw new NotSupportedException("BinaryExpression of type " + expression.NodeType + " is not supported.");
            }
        }

        protected override Expression VisitBoostBinaryExpression(BoostBinaryExpression expression)
        {
            var result = base.VisitBoostBinaryExpression(expression);

            var query = queries.Peek();

            query.SetBoost(expression.Boost);

            return result;
        }

        protected override Expression VisitLuceneQueryExpression(LuceneQueryExpression expression)
        {
            if (expression.QueryField is LuceneQueryAnyFieldExpression)
            {
                AddMultiFieldQuery(expression);

                return base.VisitLuceneQueryExpression(expression);
            }

            var mapping = fieldMappingInfoProvider.GetMappingInfo(expression.QueryField.FieldName);

            var pattern = GetPattern(expression, mapping);

            var occur = expression.Occur;
            Query query = null;
            var fieldName = mapping.FieldName;

            if (string.IsNullOrEmpty(pattern))
            {
                pattern = "*";
                occur = Negate(occur);
            }

            if (expression.QueryType == QueryType.GreaterThan || expression.QueryType == QueryType.GreaterThanOrEqual)
            {
                query = CreateRangeQuery(mapping, expression.QueryType, expression, null);
            }
            else if (expression.QueryType == QueryType.LessThan || expression.QueryType == QueryType.LessThanOrEqual)
            {
                query = CreateRangeQuery(mapping, expression.QueryType, null, expression);
            }

            if (query == null)
                query =  mapping.IsNumericField ? new TermQuery(new Term(fieldName, pattern)) : Parse(fieldName, pattern);

            var booleanQuery = new BooleanQuery();

            query.SetBoost(expression.Boost);
            booleanQuery.Add(query, occur);

            queries.Push(booleanQuery);

            return base.VisitLuceneQueryExpression(expression);
        }

        private string GetPattern(LuceneQueryExpression expression, IFieldMappingInfo mapping)
        {
            var pattern = EvaluateExpressionToString(expression, mapping);

            switch (expression.QueryType)
            {
                case QueryType.Prefix:
                    pattern += "*";
                    break;
                case QueryType.Wildcard:
                case QueryType.Suffix:
                    pattern = "*" + pattern;
                    if (expression.QueryType == QueryType.Wildcard)
                    {
                        pattern += "*";
                    }
                    break;
            }
            return pattern;
        }

        private void AddMultiFieldQuery(LuceneQueryExpression expression)
        {
            var query = new BooleanQuery();

            var parser = new MultiFieldQueryParser(context.Version,
                                                   fieldMappingInfoProvider.AllFields.ToArray(),
                                                   context.Analyzer);
            
            query.Add(new BooleanClause(parser.Parse(GetPattern(expression, null)), expression.Occur));

            queries.Push(query);
        }

        private Query CreateRangeQuery(IFieldMappingInfo mapping, QueryType queryType, LuceneQueryExpression lowerBoundExpression, LuceneQueryExpression upperBoundExpression)
        {
            var lowerRange = RangeType.Inclusive;
            var upperRange = (queryType == QueryType.LessThan || queryType == QueryType.GreaterThan) ? RangeType.Exclusive : RangeType.Inclusive;

            if (upperBoundExpression == null)
            {
                lowerRange = upperRange;
                upperRange = RangeType.Inclusive;
            }

            if (mapping.IsNumericField)
            {
                var lowerBound = lowerBoundExpression == null ? null : EvaluateExpression(lowerBoundExpression);
                var upperBound = upperBoundExpression == null ? null : EvaluateExpression(upperBoundExpression);
                return NumericRangeUtils.CreateNumericRangeQuery(mapping.FieldName, (ValueType)lowerBound, (ValueType)upperBound, lowerRange, upperRange);
            }
            else
            {
                var minInclusive = lowerRange == RangeType.Inclusive;
                var maxInclusive = upperRange == RangeType.Inclusive;

                var lowerBound = lowerBoundExpression == null ? null : EvaluateExpressionToString(lowerBoundExpression, mapping);
                var upperBound = upperBoundExpression == null ? null : EvaluateExpressionToString(upperBoundExpression, mapping);
                return new TermRangeQuery(mapping.FieldName, lowerBound, upperBound, minInclusive, maxInclusive);
            }
        }

        private static BooleanClause.Occur Negate(BooleanClause.Occur occur)
        {
            return (occur == BooleanClause.Occur.MUST_NOT)
                       ? BooleanClause.Occur.MUST
                       : BooleanClause.Occur.MUST_NOT;
        }

        private Expression MakeBooleanQuery(BinaryExpression expression)
        {
            var result = base.VisitBinaryExpression(expression);

            var second = queries.Pop();
            var first = queries.Pop();

            var query = new BooleanQuery();
            var occur = expression.NodeType == ExpressionType.AndAlso ? BooleanClause.Occur.MUST : BooleanClause.Occur.SHOULD;
            query.Add(first, occur);
            query.Add(second, occur);
            
            queries.Push(query);

            return result;
        }

        private object EvaluateExpression(LuceneQueryExpression expression)
        {
            var lambda = Expression.Lambda(expression.QueryPattern).Compile();
            return lambda.DynamicInvoke();
        }

        private string EvaluateExpressionToString(LuceneQueryExpression expression, IFieldMappingInfo mapping)
        {
            var result = EvaluateExpression(expression);

            return mapping == null ? result.ToString() : mapping.ConvertToQueryExpression(result);
        }
    }
}