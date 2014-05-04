using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Exceptions;
using EventStore.ClientAPI.SystemData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.EventSourcing.EventStoreStorage.Exceptions;
using Orleans.Storage;

namespace Orleans.EventSourcing.EventStoreStorage
{
    public class EventStoreProvider : IStorageProvider
    {
        private IEventStoreConnection Connection;

        private const string EventTypeHeader = "EventTypeName";
        private const int WritePageSize = 500;
        private const int ReadPageSize = 500;

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None };

        public string Name { get; private set; }
        public OrleansLogger Log { get; private set; }

        public Task Init(string name, Orleans.Providers.IProviderRuntime providerRuntime, Orleans.Providers.IProviderConfiguration config)
        {
            this.Name = name;
            this.Log = providerRuntime.GetLogger(this.GetType().FullName, Logger.LoggerType.Application);

            // Create EventStore connection
            var username = config.Properties.ContainsKey("username") ? config.Properties["username"] : "admin";
            var password = config.Properties.ContainsKey("password") ? config.Properties["password"] : "changeit";

            var settings = ConnectionSettings.Create()
                .KeepReconnecting().KeepRetrying()
                .SetDefaultUserCredentials(new UserCredentials(username, password));

            // Connection string format: <hostName>:<port>
            var connectionStringParts = config.Properties["ConnectionString"].Split(':'); 
            var hostName = connectionStringParts[0];
            var hostPort = int.Parse(connectionStringParts[1]);
            var hostAddress = Dns.GetHostAddresses(hostName).First(a => a.AddressFamily == AddressFamily.InterNetwork);

            this.Connection = EventStoreConnection.Create(settings, new IPEndPoint(hostAddress, hostPort));

            // Connect to EventStore
            return this.Connection.ConnectAsync();
        }

        public Task ClearStateAsync(string grainType, Orleans.GrainReference grainReference, Orleans.GrainState grainState)
        {
            if (!(grainState is IAggregateState))
                throw new NotAggregateStateException(grainState.GetType());

            var state = grainState as IAggregateState;
            var stream = this.GetStreamName(grainType, grainReference);

            return this.Connection.DeleteStreamAsync(stream, state.Version);
        }

        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (!(grainState is IAggregateState))
                throw new NotAggregateStateException(grainState.GetType());

            var stream = this.GetStreamName(grainType, grainReference);

            var sliceStart = 0;
            StreamEventsSlice currentSlice;

            do
            {
                var sliceCount = sliceStart + ReadPageSize;

                currentSlice = await this.Connection.ReadStreamEventsForwardAsync(stream, sliceStart, sliceCount, true);

                if (currentSlice.Status == SliceReadStatus.StreamNotFound)
                    throw new StreamNotFoundException();

                if (currentSlice.Status == SliceReadStatus.StreamDeleted)
                    throw new StreamDeletedException();

                sliceStart = currentSlice.NextEventNumber;

                foreach (var @event in currentSlice.Events)
                    StateTransformer.ApplyEvent(@event, grainState as IAggregateState);

            } while (!currentSlice.IsEndOfStream);
        }

        public async Task WriteStateAsync(string grainType, Orleans.GrainReference grainReference, Orleans.IGrainState grainState)
        {
            if (!(grainState is IAggregateState))
                throw new NotAggregateStateException(grainState.GetType());

            var state = grainState as IAggregateState;
            var stream = this.GetStreamName(grainType, grainReference);

            var newEvents = state.UncommitedEvents;

            if (newEvents.Count == 0)
                return;

            var originalVersion = state.Version - newEvents.Count - 1;
            var expectedVersion = originalVersion == -1 ? ExpectedVersion.NoStream : originalVersion;
            var eventsToSave = newEvents.Select(e => ToEventData(e)).ToList();

            if (eventsToSave.Count < WritePageSize)
            {
                await this.Connection.AppendToStreamAsync(stream, expectedVersion, eventsToSave.ToArray());
            }
            else
            {
                var transaction = await this.Connection.StartTransactionAsync(stream, expectedVersion);

                var position = 0;
                while (position < eventsToSave.Count)
                {
                    var pageEvents = eventsToSave.Skip(position).Take(WritePageSize);
                    await transaction.WriteAsync(pageEvents);
                    position += WritePageSize;
                }

                await transaction.CommitAsync();
            }

            state.UncommitedEvents.Clear();
        }

        public Task Close()
        {
            this.Connection.Close();

            return TaskDone.Done;
        }

        // TODO: Create extension point here
        private string GetStreamName(string grainType, Orleans.GrainReference grainReference)
        {
            return string.Concat(grainType, "-", grainReference.ToKeyString());
        }

        #region Event serialisation

        private static object DeserializeEvent(RecordedEvent @event)
        {
            var metadata = DeserializeMetadata(@event.Metadata);
            var eventTypeProperty = metadata.Property(EventTypeHeader);

            if (eventTypeProperty == null)
                return null;

            return DeserializeEvent((string)eventTypeProperty.Value, @event.Data);
        }

        private static object DeserializeEvent(string eventClrTypeName, byte[] data)
        {
            var eventType = Type.GetType((string)eventClrTypeName);
            Debug.Assert(eventType != null, "Couldn't load type '{0}'. Are you missing an assembly reference?", eventClrTypeName);

            return JsonConvert.DeserializeObject(Encoding.UTF8.GetString(data), eventType);
        }

        private static JObject DeserializeMetadata(byte[] metadata)
        {
            return JObject.Parse(Encoding.UTF8.GetString(metadata));
        }

        private static EventData ToEventData(object processedEvent)
        {
            var headers = new Dictionary<string, object>
            {
                { "EventClrTypeName", EventTypeHeader }
            };

            return ToEventData(Guid.NewGuid(), processedEvent, headers);
        }

        private static EventData ToEventData(Guid eventId, object evnt, IDictionary<string, object> headers)
        {
            var data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(evnt, SerializerSettings));

            var eventTypeName = evnt.GetType().AssemblyQualifiedName;
            var eventHeaders = new Dictionary<string, object>(headers);
            eventHeaders[EventTypeHeader] = eventTypeName;

            var metadata = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(eventHeaders, SerializerSettings));

            return new EventData(eventId, eventTypeName, true, data, metadata);
        }

        #endregion
    }
}
