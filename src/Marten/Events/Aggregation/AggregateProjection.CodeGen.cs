using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using LamarCodeGeneration;
using LamarCodeGeneration.Model;
using LamarCompiler;
using Marten.Events.CodeGeneration;
using Marten.Events.Daemon;
using Marten.Events.Projections;
using Marten.Exceptions;
using Marten.Internal;
using Marten.Schema;
using Marten.Storage;

namespace Marten.Events.Aggregation
{
    public partial class AggregateProjection<T>: GeneratedProjection, ILiveAggregatorSource<T>
    {
        private readonly Lazy<Type[]> _allEventTypes;
        private readonly ApplyMethodCollection _applyMethods;

        private readonly CreateMethodCollection _createMethods;
        private readonly string _inlineAggregationHandlerType;
        private readonly string _liveAggregationTypeName;
        private readonly ShouldDeleteMethodCollection _shouldDeleteMethods;
        private IDocumentMapping _aggregateMapping;
        private GeneratedType _inlineGeneratedType;
        private bool _isAsync;
        private GeneratedType _liveGeneratedType;
        private IAggregationRuntime _runtime;
        private Type _inlineType;
        private Type _liveType;

        public AggregateProjection(): base(typeof(T).NameInCode())
        {
            _createMethods = new CreateMethodCollection(GetType(), typeof(T));
            _applyMethods = new ApplyMethodCollection(GetType(), typeof(T));
            _shouldDeleteMethods = new ShouldDeleteMethodCollection(GetType(), typeof(T));

            ProjectionName = typeof(T).Name;

            Options.DeleteViewTypeOnTeardown<T>();

            _allEventTypes = new Lazy<Type[]>(() =>
            {
                return _createMethods.Methods.Concat(_applyMethods.Methods).Concat(_shouldDeleteMethods.Methods)
                    .Select(x => x.EventType).Concat(DeleteEvents).Concat(TransformedEvents).Distinct().ToArray();
            });


            _inlineAggregationHandlerType = GetType().ToSuffixedTypeName("InlineHandler");
            _liveAggregationTypeName = GetType().ToSuffixedTypeName("LiveAggregation");
        }

        public override Type ProjectionType => GetType();

        public bool AppliesTo(IEnumerable<Type> eventTypes)
        {
            return eventTypes
                .Intersect(AllEventTypes).Any() || eventTypes.Any(type => AllEventTypes.Any(type.CanBeCastTo));
        }

        public Type[] AllEventTypes => _allEventTypes.Value;

        Type IAggregateProjection.AggregateType => typeof(T);

        protected override bool tryAttachTypes(Assembly assembly, StoreOptions options)
        {
            _inlineType = assembly.GetExportedTypes().FirstOrDefault(x => x.Name == _inlineAggregationHandlerType);
            _liveType = assembly.GetExportedTypes().FirstOrDefault(x => x.Name == _liveAggregationTypeName);

            return _inlineType != null && _liveType != null;
        }

        protected override void assembleTypes(GeneratedAssembly assembly, StoreOptions options)
        {
            assembly.Rules.ReferenceTypes(GetType());
            assembly.ReferenceAssembly(GetType().Assembly);
            assembly.ReferenceAssembly(typeof(T).Assembly);


            assembly.Rules.ReferenceTypes(_applyMethods.ReferencedTypes().ToArray());
            assembly.Rules.ReferenceTypes(_createMethods.ReferencedTypes().ToArray());
            assembly.Rules.ReferenceTypes(_shouldDeleteMethods.ReferencedTypes().ToArray());

            // Walk the assembly dependencies for the projection and aggregate types,
            // and this will catch generic type argument dependencies as well. For GH-2061
            assembly.Rules.ReferenceTypes(GetType(), typeof(T));

            assembly.UsingNamespaces.Add("System");
            assembly.UsingNamespaces.Add("System.Linq");

            _isAsync = _createMethods.IsAsync || _applyMethods.IsAsync;

            _aggregateMapping = options.Storage.FindMapping(typeof(T));


            if (_aggregateMapping.IdMember == null)
            {
                throw new InvalidDocumentException(
                    $"No identity property or field can be determined for the aggregate '{typeof(T).FullNameInCode()}', but one is required to be used as an aggregate in projections");
            }


            buildLiveAggregationType(assembly);
            buildInlineAggregationType(assembly);
        }


        public ILiveAggregator<T> Build(StoreOptions options)
        {
            if (_liveType == null)
            {
                Compile(options);
            }

            return BuildLiveAggregator();
        }

        protected override IProjection buildProjectionObject(DocumentStore store)
        {
            return BuildRuntime(store);
        }

        internal ILiveAggregator<T> BuildLiveAggregator()
        {
            var aggregator = (ILiveAggregator<T>)Activator.CreateInstance(_liveType, this);

            foreach (var setter in _liveGeneratedType.Setters)
            {
                var prop = _liveType.GetProperty(setter.PropName);
                prop.SetValue(aggregator, setter.InitialValue);
            }

            return aggregator;
        }

        [Obsolete("Try to eliminate this in favor of IProjectionSource.Build()")]
        internal IAggregationRuntime BuildRuntime(DocumentStore store)
        {
            // You have to have the inlineGeneratedType built out to apply
            // setter values
            if (_liveType == null)
            {
                Compile(store.Options);
            }
            else if (_inlineGeneratedType == null)
            {
                var rules = store.Options.CreateGenerationRules();
                assembleTypes(new GeneratedAssembly(rules), store.Options);
            }

            var storage = store.Options.Providers.StorageFor<T>().Lightweight;
            var slicer = buildEventSlicer(store.Options);

            var inline = (IAggregationRuntime)Activator.CreateInstance(_inlineType, store, this, slicer,
                storage, this);

            /*
 {Void .ctor(Marten.IDocumentStore, Marten.Events.Aggregation.IAggregateProjection, Marten.Events.Aggregation.IEventSlicer`2[Marten.AsyncDaemon.Testing.Day,System.Int32], Marten.Storage.ITenancy, Marten.Internal.Storage.IDocumentStorage`2[Marten.AsyncDaemon.Testing.Day,System.Int32], Marten.AsyncDaemon.Testing.DayProjection)}
             */

            foreach (var setter in _inlineGeneratedType.Setters)
            {
                var prop = _inlineType.GetProperty(setter.PropName);
                prop.SetValue(inline, setter.InitialValue);
            }

            return inline;
        }

        protected virtual object buildEventSlicer(StoreOptions documentMapping)
        {
            Type slicerType = null;
            if (_aggregateMapping.IdType == typeof(Guid))
            {
                slicerType = typeof(ByStreamId<>).MakeGenericType(_aggregateMapping.DocumentType);
            }
            else if (_aggregateMapping.IdType != typeof(string))
            {
                throw new ArgumentOutOfRangeException(
                    $"{_aggregateMapping.IdType.FullNameInCode()} is not a supported stream id type for aggregate {_aggregateMapping.DocumentType.FullNameInCode()}");
            }
            else
            {
                slicerType = typeof(ByStreamKey<>).MakeGenericType(_aggregateMapping.DocumentType);
            }

            return Activator.CreateInstance(slicerType);
        }


        internal string SourceCode()
        {
            var writer = new StringWriter();
            writer.WriteLine(_liveGeneratedType.SourceCode);
            writer.WriteLine();

            writer.WriteLine(_inlineGeneratedType.SourceCode);
            writer.WriteLine();

            return writer.ToString();
        }

        [Obsolete("try to delete")]
        internal void Compile(StoreOptions options)
        {
            var rules = options.CreateGenerationRules();
            Compile(options, rules);
        }

        [Obsolete("Try to delete")]
        internal void Compile(StoreOptions options, GenerationRules rules)
        {
            // HOKEY, but makes ICodeFile functions work. Temporary, hopefully.
            StoreOptions = options;

            this.As<ICodeFile>().InitializeSynchronously(rules, options.EventGraph, null);

            // You have to do this for the sake of the Setters
            if (_liveGeneratedType == null)
            {
                assembleTypes(new GeneratedAssembly(rules), options);
            }
        }

        protected override bool needsSettersGenerated()
        {
            return _liveGeneratedType == null || _inlineGeneratedType == null;
        }


        protected virtual Type baseTypeForAggregationRuntime()
        {
            return typeof(AggregationRuntime<,>).MakeGenericType(typeof(T), _aggregateMapping.IdType);
        }

        private void buildInlineAggregationType(GeneratedAssembly assembly)
        {
            var inlineBaseType = baseTypeForAggregationRuntime();

            _inlineGeneratedType = assembly.AddType(_inlineAggregationHandlerType, inlineBaseType);

            _createMethods.BuildCreateMethod(_inlineGeneratedType, _aggregateMapping);

            _inlineGeneratedType.AllInjectedFields.Add(new InjectedField(GetType()));

            buildApplyEventMethod();

            _inlineGeneratedType.Setters.AddRange(_applyMethods.Setters());
            _inlineGeneratedType.Setters.AddRange(_createMethods.Setters());
            _inlineGeneratedType.Setters.AddRange(_shouldDeleteMethods.Setters());
        }

        private GeneratedMethod buildApplyEventMethod()
        {
            var method = _inlineGeneratedType.MethodFor(nameof(AggregationRuntime<string, string>.ApplyEvent));

            // This gets you the EventSlice aggregate Id

            method.DerivedVariables.Add(new Variable(_aggregateMapping.IdType,
                $"slice.{nameof(EventSlice<string, string>.Id)}"));
            method.DerivedVariables.Add(Variable.For<Tenant>($"slice.{nameof(EventSlice<string, string>.Tenant)}"));
            method.DerivedVariables.Add(Variable.For<Tenant>($"slice.{nameof(EventSlice<string, string>.Tenant)}"));
            method.DerivedVariables.Add(Variable.For<IEvent>("@event"));
            method.DerivedVariables.Add(
                Variable.For<IMartenSession>($"({typeof(IMartenSession).FullNameInCode()})session"));
            method.DerivedVariables.Add(Variable.For<IQuerySession>("session"));
            method.DerivedVariables.Add(
                Variable.For<IAggregateProjection>(nameof(AggregationRuntime<string, string>.Projection)));


            var eventHandlers = new LightweightCache<Type, AggregateEventProcessingFrame>(
                eventType => new AggregateEventProcessingFrame(typeof(T), eventType));

            foreach (var deleteEvent in DeleteEvents) eventHandlers[deleteEvent].AlwaysDeletes = true;

            foreach (var slot in _applyMethods.Methods) eventHandlers[slot.EventType].Apply = new ApplyMethodCall(slot);

            foreach (var slot in _createMethods.Methods)
            {
                eventHandlers[slot.EventType].CreationFrame = slot.Method is ConstructorInfo
                    ? new AggregateConstructorFrame(slot)
                    : new CreateAggregateFrame(slot);
            }

            foreach (var slot in _shouldDeleteMethods.Methods)
                eventHandlers[slot.EventType].Deletion = new ShouldDeleteFrame(slot);

            var frames = eventHandlers.OfType<EventProcessingFrame>().ToList();
            frames.Sort(new EventTypeComparer());
            var patternMatching = new EventTypePatternMatchFrame(frames);
            method.Frames.Add(patternMatching);

            method.Frames.Code("return aggregate;");

            return method;
        }

        private void buildLiveAggregationType(GeneratedAssembly assembly)
        {
            var liveBaseType = _isAsync
                ? typeof(AsyncLiveAggregatorBase<>)
                : typeof(SyncLiveAggregatorBase<>);

            liveBaseType = liveBaseType.MakeGenericType(typeof(T));


            _liveGeneratedType =
                assembly.AddType(_liveAggregationTypeName, liveBaseType);

            var overrideMethodName = _isAsync ? "BuildAsync" : "Build";
            var buildMethod = _liveGeneratedType.MethodFor(overrideMethodName);

            buildMethod.DerivedVariables.Add(Variable.For<IQuerySession>("(IQuerySession)session"));

            buildMethod.Frames.Code("if (!events.Any()) return null;");

            buildMethod.Frames.Add(new DeclareAggregateFrame(typeof(T)));


            var callCreateAggregateFrame = new CallCreateAggregateFrame(_createMethods);

            // This is the existing snapshot passed into the LiveAggregator
            var snapshot = buildMethod.Arguments.Single(x => x.VariableType == typeof(T));
            callCreateAggregateFrame.CoalesceAssignTo(snapshot);

            buildMethod.Frames.Add(callCreateAggregateFrame);
            buildMethod.Frames.Add(new CallApplyAggregateFrame(_applyMethods) {InsideForEach = true});

            buildMethod.Frames.Return(typeof(T));

            _liveGeneratedType.AllInjectedFields.Add(new InjectedField(GetType()));

            _createMethods.BuildCreateMethod(_liveGeneratedType, _aggregateMapping);
            _applyMethods.BuildApplyMethod(_liveGeneratedType, _aggregateMapping);

            _liveGeneratedType.Setters.AddRange(_applyMethods.Setters());
            _liveGeneratedType.Setters.AddRange(_createMethods.Setters());
            _liveGeneratedType.Setters.AddRange(_shouldDeleteMethods.Setters());
        }

        internal override void CompileAndAssertValidity()
        {
            if (_applyMethods.IsEmpty() && _createMethods.IsEmpty())
            {
                throw new InvalidProjectionException(
                    $"AggregateProjection for {typeof(T).FullNameInCode()} has no valid create or apply operations");
            }

            var invalidMethods =
                MethodCollection.FindInvalidMethods(GetType(), _applyMethods, _createMethods, _shouldDeleteMethods);

            if (invalidMethods.Any())
            {
                throw new InvalidProjectionException(this, invalidMethods);
            }

            specialAssertValid();

            var eventTypes = determineEventTypes();
            IncludedEventTypes.Fill(eventTypes);
        }

        protected virtual void specialAssertValid()
        {
        }

        internal override IEnumerable<string> ValidateConfiguration(StoreOptions options)
        {
            var mapping = options.Storage.FindMapping(typeof(T)).Root.As<DocumentMapping>();
            foreach (var p in validateDocumentIdentity(options, mapping)) yield return p;

            if (options.Events.TenancyStyle != mapping.TenancyStyle)
            {
                yield return
                    $"Tenancy storage style mismatch between the events ({options.Events.TenancyStyle}) and the aggregate type {typeof(T).FullNameInCode()} ({mapping.TenancyStyle})";
            }

            if (mapping.DeleteStyle == DeleteStyle.SoftDelete)
            {
                yield return
                    $"AggregateProjection cannot support aggregates that are soft-deleted";
            }

        }

        protected virtual IEnumerable<string> validateDocumentIdentity(StoreOptions options, DocumentMapping mapping)
        {
            if (options.Events.StreamIdentity == StreamIdentity.AsGuid)
            {
                if (mapping.IdType != typeof(Guid))
                {
                    yield return
                        $"Id type mismatch. The stream identity type is System.Guid, but the aggregate document {typeof(T).FullNameInCode()} id type is {mapping.IdType.NameInCode()}";
                }
            }

            if (options.Events.StreamIdentity == StreamIdentity.AsString)
            {
                if (mapping.IdType != typeof(string))
                {
                    yield return
                        $"Id type mismatch. The stream identity type is string, but the aggregate document {typeof(T).FullNameInCode()} id type is {mapping.IdType.NameInCode()}";
                }
            }
        }

        protected virtual Type[] determineEventTypes()
        {
            var eventTypes = MethodCollection.AllEventTypes(_applyMethods, _createMethods, _shouldDeleteMethods)
                .Concat(DeleteEvents).Concat(TransformedEvents).Distinct().ToArray();
            return eventTypes;
        }

        protected override ValueTask<EventRangeGroup> groupEvents(DocumentStore store, IMartenDatabase daemonDatabase, EventRange range,
            CancellationToken cancellationToken)
        {
            _runtime ??= BuildRuntime(store);

            return _runtime.GroupEvents(store, daemonDatabase, range, cancellationToken);
        }


        /// <summary>
        /// When used as an asynchronous projection, this opts into
        /// only taking in events from streams explicitly marked as being
        /// the aggregate type for this projection. Only use this if you are explicitly
        /// marking streams with the aggregate type on StartStream()
        /// </summary>
        [MartenIgnore]
        public void FilterIncomingEventsOnStreamType()
        {
            StreamType = typeof(T);
        }
    }
}
