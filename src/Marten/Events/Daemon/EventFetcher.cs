using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Baseline.Dates;
using Marten.Internal.Sessions;
using Marten.Linq.QueryHandlers;
using Marten.Services;
using Marten.Storage;
using Npgsql;
using NpgsqlTypes;
using Weasel.Postgresql;
using Weasel.Postgresql.SqlGeneration;

namespace Marten.Events.Daemon
{
    /// <summary>
    /// Fetches ranges of event objects. Used within the asynchronous projections
    /// </summary>
    internal class EventFetcher : IDisposable
    {
        private readonly IDocumentStore _store;
        private readonly IMartenDatabase _database;
        private readonly ISqlFragment[] _filters;
        private readonly IEventStorage _storage;
        private readonly NpgsqlParameter _floor;
        private readonly NpgsqlParameter _ceiling;
        private readonly NpgsqlCommand _command;
        private readonly int _aggregateIndex;

        public EventFetcher(IDocumentStore store, IMartenDatabase database, ISqlFragment[] filters)
        {
            _store = store;
            _database = database;
            _filters = filters;

            using var session = querySession();
            _storage = session.EventStorage();

            var schemaName = store.Options.Events.DatabaseSchemaName;

            var builder = new CommandBuilder();
            builder.Append($"select {_storage.SelectFields().Select(x => "d." + x).Join(", ")}, s.type as stream_type");
            builder.Append($" from {schemaName}.mt_events as d inner join {schemaName}.mt_streams as s on d.stream_id = s.id");

            if (_store.Options.Events.TenancyStyle == TenancyStyle.Conjoined)
            {
                builder.Append(" and d.tenant_id = s.tenant_id");
            }

            var parameters = builder.AppendWithParameters($" where d.seq_id > ? and d.seq_id <= ?");
            _floor = parameters[0];
            _ceiling = parameters[1];
            _floor.NpgsqlDbType = _ceiling.NpgsqlDbType = NpgsqlDbType.Bigint;

            foreach (var filter in filters)
            {
                builder.Append(" and ");
                filter.Apply(builder);
            }

            _command = builder.Compile();
            _aggregateIndex = _storage.SelectFields().Length;
        }

        private QuerySession querySession()
        {
            return (QuerySession)_store.QuerySession(SessionOptions.ForDatabase(_database));
        }

        private void teardown()
        {

        }

        public void Dispose()
        {
            teardown();
        }

        public async Task Load(EventRange range, CancellationToken token)
        {
            // There's an assumption here that this method is only called sequentially
            // and never at the same time on the same instance

            try
            {
                range.Events = new List<IEvent>();

                using var session = querySession();
                _floor.Value = range.SequenceFloor;
                _ceiling.Value = range.SequenceCeiling;

                using var reader = await session.ExecuteReaderAsync(_command, token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var @event = await _storage.ResolveAsync(reader, token).ConfigureAwait(false);

                    if (!await reader.IsDBNullAsync(_aggregateIndex, token).ConfigureAwait(false))
                    {
                        @event.AggregateTypeName = await reader.GetFieldValueAsync<string>(_aggregateIndex, token).ConfigureAwait(false);
                    }

                    range.Events.Add(@event);
                }



            }
            catch (Exception e)
            {
                throw new EventFetcherException(range.ShardName, _database, e);
            }

            Debug.WriteLine("here");
        }
    }
}
