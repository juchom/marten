using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Baseline.Dates;
using Marten.Events.Daemon;

namespace Marten.Events.TestSupport
{
    public partial class ProjectionScenario: IEventOperations
    {
        private readonly Queue<ScenarioStep> _steps = new();
        private readonly DocumentStore _store;


        public ProjectionScenario(DocumentStore store)
        {
            _store = store;
        }

        internal IProjectionDaemon Daemon { get; private set; }

        internal ScenarioStep NextStep => _steps.Any() ? _steps.Peek() : null;

        internal IDocumentSession Session { get; private set; }

        /// <summary>
        ///     Disable the scenario from "cleaning" out any existing
        ///     event and projected document data before running the scenario
        /// </summary>
        public bool DoNotDeleteExistingData { get; set; }



        internal Task WaitForNonStaleData()
        {
            if (Daemon == null)
            {
                return Task.CompletedTask;
            }

            return Daemon.WaitForNonStaleData(30.Seconds());
        }


        private ScenarioStep action(Action<IEventOperations> action)
        {
            var step = new ScenarioAction(action);
            _steps.Enqueue(step);

            return step;
        }

        private ScenarioStep assertion(Func<IQuerySession, Task> check)
        {
            var step = new ScenarioAssertion(check);
            _steps.Enqueue(step);

            return step;
        }

        internal async Task Execute()
        {
            if (!DoNotDeleteExistingData)
            {
                await _store.Advanced.Clean.DeleteAllEventDataAsync().ConfigureAwait(false);
                foreach (var storageType in
                    _store.Options.Projections.All.SelectMany(x => x.Options.StorageTypes))
                    await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(storageType).ConfigureAwait(false);
            }

            if (_store.Options.Projections.HasAnyAsyncProjections())
            {
                Daemon = await _store.BuildProjectionDaemonAsync().ConfigureAwait(false);
                await Daemon.StartAllShards().ConfigureAwait(false);
            }

            Session = _store.LightweightSession();

            try
            {
                var exceptions = new List<Exception>();
                var number = 0;
                var descriptions = new List<string>();

                while (_steps.Any())
                {
                    number++;
                    var step = _steps.Dequeue();

                    try
                    {
                        await step.Execute(this).ConfigureAwait(false);
                        descriptions.Add($"{number.ToString().PadLeft(3)}. {step.Description}");
                    }
                    catch (Exception e)
                    {
                        descriptions.Add($"FAILED: {number.ToString().PadLeft(3)}. {step.Description}");
                        descriptions.Add(e.ToString());
                        exceptions.Add(e);
                    }
                }

                if (exceptions.Any())
                {
                    throw new ProjectionScenarioException(descriptions, exceptions);
                }
            }
            finally
            {
                if (Daemon != null)
                {
                    await Daemon.StopAll().ConfigureAwait(false);
                    Daemon.SafeDispose();
                }

                Session?.SafeDispose();
            }
        }
    }
}
