// <auto-generated/>
#pragma warning disable
using Marten.Internal.CompiledQueries;
using Marten.Linq;
using Marten.Linq.QueryHandlers;
using Marten.Testing.Bugs;
using System;

namespace Marten.Generated.CompiledQueries
{
    // START: QueryOnlyFunnyTargetQueryCompiledQuery127536733
    public class QueryOnlyFunnyTargetQueryCompiledQuery127536733 : Marten.Internal.CompiledQueries.ClonedCompiledQuery<System.Collections.Generic.IEnumerable<Marten.Testing.Documents.Target>, Marten.Testing.Bugs.Bug_784_Collection_Contains_within_compiled_query.FunnyTargetQuery>
    {
        private readonly Marten.Linq.QueryHandlers.IMaybeStatefulHandler _inner;
        private readonly Marten.Testing.Bugs.Bug_784_Collection_Contains_within_compiled_query.FunnyTargetQuery _query;
        private readonly Marten.Linq.QueryStatistics _statistics;
        private readonly Marten.Internal.CompiledQueries.HardCodedParameters _hardcoded;

        public QueryOnlyFunnyTargetQueryCompiledQuery127536733(Marten.Linq.QueryHandlers.IMaybeStatefulHandler inner, Marten.Testing.Bugs.Bug_784_Collection_Contains_within_compiled_query.FunnyTargetQuery query, Marten.Linq.QueryStatistics statistics, Marten.Internal.CompiledQueries.HardCodedParameters hardcoded) : base(inner, query, statistics, hardcoded)
        {
            _inner = inner;
            _query = query;
            _statistics = statistics;
            _hardcoded = hardcoded;
        }



        public override void ConfigureCommand(Weasel.Postgresql.CommandBuilder builder, Marten.Internal.IMartenSession session)
        {
            var parameters = builder.AppendWithParameters(@"WITH mt_temp_id_list1CTE as (
            select ctid, unnest(CAST(ARRAY(SELECT jsonb_array_elements_text(CAST(d.data ->> 'NumberArray' as jsonb))) as integer[])) as data from public.mt_doc_target as d WHERE (d.data ->> 'Flag' is not null and CAST(d.data ->> 'Flag' as boolean) = True)
            )
              , mt_temp_id_list2CTE as (
            select ctid from mt_temp_id_list1CTE where data = ?
            )
             select d.data from public.mt_doc_target as d where ctid in (select ctid from mt_temp_id_list2CTE)");

            parameters[0].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer;
            parameters[0].Value = _query.Number;
        }

    }

    // END: QueryOnlyFunnyTargetQueryCompiledQuery127536733
    
    
    // START: QueryOnlyFunnyTargetQueryCompiledQuerySource127536733
    public class QueryOnlyFunnyTargetQueryCompiledQuerySource127536733 : Marten.Internal.CompiledQueries.CompiledQuerySource<System.Collections.Generic.IEnumerable<Marten.Testing.Documents.Target>, Marten.Testing.Bugs.Bug_784_Collection_Contains_within_compiled_query.FunnyTargetQuery>
    {
        private readonly Marten.Internal.CompiledQueries.HardCodedParameters _hardcoded;
        private readonly Marten.Linq.QueryHandlers.IMaybeStatefulHandler _maybeStatefulHandler;

        public QueryOnlyFunnyTargetQueryCompiledQuerySource127536733(Marten.Internal.CompiledQueries.HardCodedParameters hardcoded, Marten.Linq.QueryHandlers.IMaybeStatefulHandler maybeStatefulHandler)
        {
            _hardcoded = hardcoded;
            _maybeStatefulHandler = maybeStatefulHandler;
        }



        public override Marten.Linq.QueryHandlers.IQueryHandler<System.Collections.Generic.IEnumerable<Marten.Testing.Documents.Target>> BuildHandler(Marten.Testing.Bugs.Bug_784_Collection_Contains_within_compiled_query.FunnyTargetQuery query, Marten.Internal.IMartenSession session)
        {
            return new Marten.Generated.CompiledQueries.QueryOnlyFunnyTargetQueryCompiledQuery127536733(_maybeStatefulHandler, query, null, _hardcoded);
        }

    }

    // END: QueryOnlyFunnyTargetQueryCompiledQuerySource127536733
    
    
}
