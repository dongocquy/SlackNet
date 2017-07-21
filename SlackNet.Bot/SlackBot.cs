﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using SlackNet.Events;
using SlackNet.WebApi;

namespace SlackNet.Bot
{
    public class SlackBot : IObservable<IMessage>, IObserver<BotMessage>, IDisposable
    {
        private readonly SlackRtmClient _rtm;
        private readonly SlackApiClient _api;
        private readonly ConcurrentQueue<IMessageHandler> _handlers = new ConcurrentQueue<IMessageHandler>();
        private readonly ConcurrentDictionary<string, Task<Hub>> _hubs = new ConcurrentDictionary<string, Task<Hub>>();
        private readonly ConcurrentValue<Task<IReadOnlyList<Channel>>> _channels = new ConcurrentValue<Task<IReadOnlyList<Channel>>>();
        private readonly ConcurrentValue<Task<IReadOnlyList<Channel>>> _groups = new ConcurrentValue<Task<IReadOnlyList<Channel>>>();
        private readonly ConcurrentValue<Task<IReadOnlyList<Channel>>> _mpims = new ConcurrentValue<Task<IReadOnlyList<Channel>>>();
        private readonly ConcurrentDictionary<string, Task<User>> _users = new ConcurrentDictionary<string, Task<User>>();
        private readonly ConcurrentValue<Task<IReadOnlyList<User>>> _allUsers = new ConcurrentValue<Task<IReadOnlyList<User>>>();
        private readonly ConcurrentValue<Task<IReadOnlyList<Im>>> _ims = new ConcurrentValue<Task<IReadOnlyList<Im>>>();
        private readonly SyncedSubject<IMessage> _incomingMessages = new SyncedSubject<IMessage>();
        private readonly SyncedSubject<BotMessage> _outgoingMessages = new SyncedSubject<BotMessage>();
        private readonly SyncedSubject<BotMessage> _sentMessages = new SyncedSubject<BotMessage>();
        private IObservable<IMessage> _incomingWithMiddlewareApplied;
        private IObservable<BotMessage> _outgoingWithMiddlewareApplied;
        private IDisposable _outgoingSubscription;
        private IDisposable _incomingSubscription;

        public SlackBot(string token) : this(new SlackRtmClient(token), new SlackApiClient(token)) { }

        public SlackBot(SlackRtmClient rtmClient, SlackApiClient apiClient)
        {
            _rtm = rtmClient;
            _api = apiClient;
            _incomingWithMiddlewareApplied = _rtm.Messages
                .Where(m => m.Subtype == null)
                .SelectMany(CreateSlackMessage);
            _outgoingWithMiddlewareApplied = _outgoingMessages
                .LimitFrequency(TimeSpan.FromSeconds(1));
        }

        public string Id { get; private set; }
        public string Name { get; private set; }

        public async Task Connect(CancellationToken? cancellationToken = null)
        {
            // If already connected, client will throw
            var connection = _rtm.Connect(cancellationToken: cancellationToken);

            _incomingSubscription = _incomingWithMiddlewareApplied
                .Subscribe(HandleMessage);
            _outgoingSubscription = _outgoingWithMiddlewareApplied
                .SelectMany(PostMessage)
                .Retry()
                .Subscribe();

            var connectResponse = await connection.ConfigureAwait(false);
            Id = connectResponse.Self.Id;
            Name = connectResponse.Self.Name;
        }

        public void AddIncomingMiddleware(Func<IObservable<IMessage>, IObservable<IMessage>> middleware)
        {
            if (_rtm.Connected)
                throw new InvalidOperationException("Can't add more middleware after bot is connected.");

            _incomingWithMiddlewareApplied = middleware(_incomingWithMiddlewareApplied);
        }

        public void AddOutgoingMiddleware(Func<IObservable<BotMessage>, IObservable<BotMessage>> middleware)
        {
            if (_rtm.Connected)
                throw new InvalidOperationException("Can't add more middleware after bot is connected.");

            _outgoingWithMiddlewareApplied = middleware(_outgoingWithMiddlewareApplied);
        }

        public void AddHandler(IMessageHandler handler) => _handlers.Enqueue(handler);
        public event EventHandler<IMessage> OnMessage;
        public IObservable<IMessage> Messages => _incomingMessages.AsObservable();

        private async Task<SlackMessage> CreateSlackMessage(MessageEvent message) =>
            new SlackMessage(message, this)
                {
                    Ts = message.Ts,
                    Text = message.Text,
                    Hub = await GetHubById(message.Channel).ConfigureAwait(false),
                    User = await GetUserById(message.User).ConfigureAwait(false),
                    Attachments = message.Attachments
                };

        private void HandleMessage(IMessage message)
        {
            OnMessage?.Invoke(this, message);
            _handlers
                .ToList()
                .ForEach(h => h.HandleMessage(message));
            _incomingMessages.OnNext(message);
        }

        public async Task<Hub> GetHubById(string channelId) => await _hubs.GetOrAdd(channelId, FetchHub).ConfigureAwait(false);

        private async Task<Hub> FetchHub(string channelId) => 
              channelId[0] == 'C' ? await _api.Channels.Info(channelId).NullIfNotFound().ConfigureAwait(false) 
            : channelId[0] == 'G' ? await _api.Groups.Info(channelId).NullIfNotFound().ConfigureAwait(false)
            : channelId[0] == 'D' ? await FetchImChannel(channelId).ConfigureAwait(false) 
            : null;

        private async Task<Hub> FetchImChannel(string channelId)
        {
            IReadOnlyList<Im> ims = await GetOpenIms().ConfigureAwait(false);
            Im matchingIm = ims.FirstOrDefault(im => im.Id == channelId);
            if (matchingIm == null)
                return null;

            ImResponse fullImResponse = await _api.Im.Open(matchingIm.User, true).NullIfNotFound().ConfigureAwait(false);
            return fullImResponse?.Channel;
        }

        /// <summary>
        /// Find hub with matching name.
        /// </summary>
        /// <param name="channel">Channel, group or IM name, with leading # or @ symbol as appropriate.</param>
        public Task<Hub> GetHubByName(string channel) => 
              channel.FirstOrDefault() == '#' ? GetChannelByName(channel.Substring(1))
            : channel.FirstOrDefault() == '@' ? GetImByName(channel.Substring(1))
            : GetGroupByName(channel);

        public async Task<Hub> GetChannelByName(string name) =>
            await FindCachedHub(h => h.IsChannel && h.Name == name).ConfigureAwait(false)
            ?? (await GetChannels().ConfigureAwait(false))
                .FirstOrDefault(c => c.Name == name);

        public async Task<Hub> GetGroupByName(string name) =>
            await FindCachedHub(h => h.IsGroup && h.Name == name).ConfigureAwait(false)
            ?? (await GetGroups().ConfigureAwait(false))
                .FirstOrDefault(g => g.Name == name);

        private async Task<Channel> FindCachedHub(Func<Channel, bool> predicate) =>
            await _hubs.Values.ToObservable()
                .SelectMany(h => h)
                .OfType<Channel>()
                .FirstOrDefaultAsync(predicate)
                .ToTask()
                .ConfigureAwait(false);

        public async Task<Hub> GetImByName(string username) =>
            await (await GetOpenIms().ConfigureAwait(false))
                .ToObservable()
                .SelectMany(async im => new { im, user = await GetUserById(im.User).ConfigureAwait(false) })
                .FirstOrDefaultAsync(im => im.user.Name == username)
                .Select(im => im.im)
                .ToTask()
                .ConfigureAwait(false);

        public Task<IReadOnlyList<Channel>> GetChannels() => _channels.GetOrCreateValue(FetchChannels);
        private async Task<IReadOnlyList<Channel>> FetchChannels() => CacheHubs(await _api.Channels.List().ConfigureAwait(false));

        public Task<IReadOnlyList<Channel>> GetGroups() => _groups.GetOrCreateValue(FetchGroups);
        private async Task<IReadOnlyList<Channel>> FetchGroups() => CacheHubs(await _api.Groups.List().ConfigureAwait(false));

        public Task<IReadOnlyList<Channel>> GetOpenMpIms() => _mpims.GetOrCreateValue(FetchMpims);
        private async Task<IReadOnlyList<Channel>> FetchMpims() => CacheHubs(await _api.Mpim.List().ConfigureAwait(false));

        private IReadOnlyList<Channel> CacheHubs(IReadOnlyList<Channel> channels)
        {
            foreach (var channel in channels)
                _hubs[channel.Id] = Task.FromResult((Hub)channel);
            return channels;
        }

        public async Task<IReadOnlyList<Im>> GetOpenIms() => await _ims.GetOrCreateValue(() => _api.Im.List()).ConfigureAwait(false);

        public Task<User> GetUserById(string userId) => _users.GetOrAdd(userId, _ => _api.Users.Info(userId).NullIfNotFound());

        public async Task<User> GetUserByName(string username) => (await GetUsers().ConfigureAwait(false)).FirstOrDefault(u => u.Name == username);

        public Task<IReadOnlyList<User>> GetUsers() => _allUsers.GetOrCreateValue(FetchUsers);

        private async Task<IReadOnlyList<User>> FetchUsers()
        {
            List<User> users = new List<User>();
            string cursor = null;
            do
            {
                var response = await _api.Users.List(cursor).ConfigureAwait(false);
                users.AddRange(response.Members);
                cursor = response.ResponseMetadata.NextCursor;
            } while (cursor != null);
            return users;
        }

        public Task Send(BotMessage message)
        {
            var sent = _sentMessages.FirstOrDefaultAsync(m => m == message).ToTask();
            _outgoingMessages.OnNext(message);
            return sent;
        }

        private async Task<PostMessageResponse> PostMessage(BotMessage message) =>
            await _api.Chat.PostMessage(new Message
                {
                    Channel = message.Hub != null
                        ? await message.Hub.HubId(this)
                        : message.ReplyTo?.Hub.Id,
                    Text = message.Text,
                    Attachments = message.Attachments,
                    ThreadTs = message.Hub != null && await message.Hub.HubId(this) != message.ReplyTo?.Hub.Id
                        ? null
                        : message.ReplyTo?.ThreadTs ?? message.ReplyTo?.Ts,
                    ReplyBroadcast = message.ReplyBroadcast,
                    Parse = message.Parse,
                    LinkNames = message.LinkNames,
                    UnfurlLinks = message.UnfurlLinks,
                    UnfurlMedia = message.UnfurlMedia,
                    AsUser = true
                });

        public async Task WhileTyping(string channelId, Func<Task> action)
        {
            using (Observable.Interval(TimeSpan.FromSeconds(4)).Subscribe(_ => _rtm.SendTyping(channelId)))
                await action().ConfigureAwait(false);
        }

        public void ClearCache()
        {
            _hubs.Clear();
            _channels.Clear();
            _groups.Clear();
            _mpims.Clear();
            _users.Clear();
            _allUsers.Clear();
            _ims.Clear();
        }

        public IDisposable Subscribe(IObserver<IMessage> observer) => _incomingMessages.Subscribe(observer);

        public void OnCompleted() => _outgoingMessages.OnCompleted();
        public void OnError(Exception error) => _outgoingMessages.OnError(error);
        public void OnNext(BotMessage value) => _outgoingMessages.OnNext(value);

        public void Dispose()
        {
            _rtm.Dispose();
            _incomingMessages.Dispose();
            _outgoingMessages.Dispose();
            _incomingSubscription?.Dispose();
            _outgoingSubscription?.Dispose();
        }
    }
}