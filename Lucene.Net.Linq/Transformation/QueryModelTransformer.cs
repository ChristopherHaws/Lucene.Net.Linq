﻿using System.Collections.Generic;
using Lucene.Net.Linq.Expressions;
using Lucene.Net.Linq.Transformation.TreeVisitors;
using Lucene.Net.Linq.Util;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Parsing;

namespace Lucene.Net.Linq.Transformation
{
    /// <summary>
    /// Transforms various expressions in a QueryModel instance to make it easier to convert into a Lucene Query.
    /// </summary>
    internal class QueryModelTransformer : QueryModelVisitorBase
    {
        private readonly IEnumerable<ExpressionTreeVisitor> whereSelectClauseVisitors;
        private readonly IEnumerable<ExpressionTreeVisitor> orderingVisitors;

        internal QueryModelTransformer()
            : this(new ExpressionTreeVisitor[]
                       {
                           new SubQueryContainsTreeVisitor(),
                           new LuceneExtensionMethodCallTreeVisitor(),
                           new QuerySourceReferenceGetMethodTransformingTreeVisitor(),
                           new QuerySourceReferencePropertyTransformingTreeVisitor(),
                           new BoostMethodCallTreeVisitor(0),
                           new FlagToBinaryConditionTreeVisitor(),
                           new NoOpMethodCallRemovingTreeVisitor(),
                           new NoOpConditionRemovingTreeVisitor(),
                           new MethodCallToBinaryExpressionTreeVisitor(),
                           new NullSafetyConditionRemovingTreeVisitor(),
                           new CompareCallToBinaryExpressionTreeVisitor(),
                           new BinaryToQueryExpressionTreeVisitor(),
                           new BoostMethodCallTreeVisitor(1)
                       },
                   new ExpressionTreeVisitor[]
                       {
                           new LuceneExtensionMethodCallTreeVisitor(),
                           new BoostMethodCallTreeVisitor(1),
                           new QuerySourceReferenceGetMethodTransformingTreeVisitor(),
                           new QuerySourceReferencePropertyTransformingTreeVisitor(),
                           new NoOpMethodCallRemovingTreeVisitor(),
                           new NullSafetyConditionRemovingTreeVisitor(),
                           new ConcatToCompositeOrderingExpressionTreeVisitor()
                       })
        {
        }

        internal QueryModelTransformer(IEnumerable<ExpressionTreeVisitor> whereSelectClauseVisitors, IEnumerable<ExpressionTreeVisitor> orderingVisitors)
        {
            this.whereSelectClauseVisitors = whereSelectClauseVisitors;
            this.orderingVisitors = orderingVisitors;
        }

        public static void TransformQueryModel(QueryModel queryModel)
        {
            var instance = new QueryModelTransformer();

            queryModel.Accept(instance);

        }

        public override void VisitWhereClause(WhereClause whereClause, QueryModel queryModel, int index)
        {
            Log.Trace(() => "Original QueryModel:     " + queryModel);

            foreach (var visitor in whereSelectClauseVisitors)
            {
                whereClause.TransformExpressions(visitor.VisitExpression);
                Log.Trace(() => "Transformed QueryModel after " + visitor.GetType().Name + ": " + queryModel);
            }

            base.VisitWhereClause(whereClause, queryModel, index);
        }

        public override void VisitOrderByClause(OrderByClause orderByClause, QueryModel queryModel, int index)
        {
            Log.Trace(() => "Original QueryModel:     " + queryModel);

            foreach (var visitor in orderingVisitors)
            {
                orderByClause.TransformExpressions(visitor.VisitExpression);
                Log.Trace(() => "Transformed QueryModel after " + visitor.GetType().Name + ": " + queryModel);
            }
            
            ExpandCompositeOrderings(orderByClause);

            base.VisitOrderByClause(orderByClause, queryModel, index);
        }

        private void ExpandCompositeOrderings(OrderByClause orderByClause)
        {
            var orderings = orderByClause.Orderings;
            var copy = new Ordering[orderings.Count];
            orderings.CopyTo(copy, 0);

            copy.Apply(o => orderings.Remove(o));

            foreach (var o in copy)
            {
                if (o.Expression is LuceneCompositeOrderingExpression)
                {
                    var ex = (LuceneCompositeOrderingExpression) o.Expression;

                    ex.Fields.Apply(f => orderings.Add(new Ordering(f, o.OrderingDirection)));
                }
                else
                {
                    orderings.Add(o);
                }
            }
        }
    }
}