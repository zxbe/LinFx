﻿using LinFx.Extensions.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LinFx.Extensions.EventBus.RabbitMQ
{
    public class RabbitMqDistributedEventBus : IEventBus
    {
        private readonly IEventBusSubscriptionsManager _subsManager;
        protected IServiceScope ServiceScope { get; }
        protected DistributedEventBusOptions DistributedEventBusOptions { get; }
        protected RabbitMqDistributedEventBusOptions RabbitMqDistributedEventBusOptions { get; }
        protected IConnectionPool ConnectionPool { get; }
        protected IRabbitMqSerializer Serializer { get; }
        protected IRabbitMqMessageConsumerFactory MessageConsumerFactory { get; }
        protected IRabbitMqMessageConsumer Consumer { get; }

        public RabbitMqDistributedEventBus(
            IOptions<DistributedEventBusOptions> distributedEventBusOptions,
            IOptions<RabbitMqDistributedEventBusOptions> rabbitMqDistributedEventBusOptions,
            IConnectionPool connectionPool, 
            IRabbitMqMessageConsumerFactory messageConsumerFactory,
            IRabbitMqSerializer serializer,
            IEventBusSubscriptionsManager subscriptionsManager,
            IServiceScopeFactory serviceScopeFactory)
        {
            RabbitMqDistributedEventBusOptions = rabbitMqDistributedEventBusOptions.Value;
            DistributedEventBusOptions = distributedEventBusOptions.Value;
            ConnectionPool = connectionPool;
            MessageConsumerFactory = messageConsumerFactory;
            Serializer = serializer;
            ServiceScope = serviceScopeFactory.CreateScope();
            _subsManager = subscriptionsManager;
            _subsManager.OnEventRemoved += SubsManager_OnEventRemoved;

            Consumer = MessageConsumerFactory.Create(
                new ExchangeDeclareConfiguration(
                    RabbitMqDistributedEventBusOptions.ExchangeName,
                        type: "direct",
                        durable: true),
                new QueueDeclareConfiguration(
                        RabbitMqDistributedEventBusOptions.ClientName,
                        durable: true,
                        exclusive: false,
                        autoDelete: false),
                RabbitMqDistributedEventBusOptions.ConnectionName
            );
            Consumer.OnMessageReceived(ProcessEventAsync);
        }

        private void SubsManager_OnEventRemoved(object sender, string e)
        {
            //Consumer.UnbindAsync("");
        }

        public Task PublishAsync(IntegrationEvent evt)
        {
            var eventName = evt.GetType().Name;
            var body = Serializer.Serialize(evt);

            using (var channel = ConnectionPool.Get(RabbitMqDistributedEventBusOptions.ConnectionName).CreateModel())
            {
                channel.ExchangeDeclare(
                    RabbitMqDistributedEventBusOptions.ExchangeName,
                    "direct",
                    durable: true
                );

                var properties = channel.CreateBasicProperties();
                properties.DeliveryMode = RabbitMqConsts.DeliveryModes.Persistent;

                channel.BasicPublish(
                   exchange: RabbitMqDistributedEventBusOptions.ExchangeName,
                    routingKey: eventName,
                    mandatory: true,
                    basicProperties: properties,
                    body: body
                );
            }

            return Task.CompletedTask;
        }

        public void Subscribe<TEvent, THandler>()
            where TEvent : IntegrationEvent
            where THandler : IIntegrationEventHandler<TEvent>
        {
            var eventName = _subsManager.GetEventKey<TEvent>();
            var containsKey = _subsManager.HasSubscriptionsForEvent(eventName);
            if (!containsKey)
            {
                _subsManager.AddSubscription<TEvent, THandler>();
                Consumer.BindAsync(eventName);
            }
        }

        public void Unsubscribe<TEvent, THandler>()
            where TEvent : IntegrationEvent
            where THandler : IIntegrationEventHandler<TEvent>
        {
            throw new NotImplementedException();
        }

        public void UnsubscribeDynamic<TH>(string eventName) where TH : IDynamicIntegrationEventHandler
        {
            throw new NotImplementedException();
        }

        private async Task ProcessEventAsync(IModel channel, BasicDeliverEventArgs ea)
        {
            var eventName = ea.RoutingKey;
            var eventData = Encoding.UTF8.GetString(ea.Body);
            await TriggerHandlersAsync(eventName, eventData);
        }

        public virtual async Task TriggerHandlersAsync(string eventName, string eventData)
        {
            var exceptions = new List<Exception>();

            await TriggerHandlersAsync(eventName, eventData, exceptions);

            if (exceptions.Any())
            {
                if (exceptions.Count == 1)
                {
                    exceptions[0].ReThrow();
                }

                throw new AggregateException("More than one error has occurred while triggering the event: " + eventName, exceptions);
            }
        }

        protected virtual async Task TriggerHandlersAsync(string eventName, string eventData, List<Exception> exceptions)
        {
            if (_subsManager.HasSubscriptionsForEvent(eventName))
            {
                var subscriptions = _subsManager.GetHandlersForEvent(eventName);
                foreach (var subscription in subscriptions)
                {
                    try
                    {
                        var eventType = _subsManager.GetEventTypeByName(eventName);
                        var integrationEvent = JsonConvert.DeserializeObject(eventData, eventType);
                        var handler = ServiceScope.ServiceProvider.GetService(subscription.HandlerType);
                        var concreteType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
                        await (Task)concreteType.GetMethod("Handle").Invoke(handler, new object[] { integrationEvent });
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }
        }

    }
}