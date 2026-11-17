using Blp.Subscriptions;
using static Blp.Tests.Subscriptions.MarketDataEvents;

namespace Blp.Tests.Subscriptions;

public class SubscriptionTests
{
    private const string Aaa = "AAA US Equity";
    private const string Bbb = "BBB US Equity";
    private const string Ccc = "CCC US Equity";

    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(5);

    /// <summary>Add subscribes only new topics, each under its own id.</summary>
    /// <remarks>
    /// Mutation: Add subscribing held topics again, or one id for every topic.
    /// Oracle: two calls, the second naming one held and one new topic.
    /// </remarks>
    [Fact]
    public void AddSkipsHeldTopicsAndUsesFreshIds()
    {
        var harness = new SubscriptionHarness();

        harness.Subscription.Add(Aaa, Bbb);
        harness.Subscription.Add(Aaa, Ccc);

        var (first, second) = (harness.Ids(0), harness.Ids(1));
        Assert.Equal([Aaa, Bbb], first.Keys);
        Assert.Equal([Ccc], second.Keys);
        Assert.Equal(3, first.Values.Concat(second.Values).Distinct().Count());
    }

    /// <summary>
    /// Events for a removed topic's old id never reach the re-added topic.
    /// </summary>
    /// <remarks>
    /// Mutation: the id mapping kept after the unsubscribe, or the old id reused on
    /// re-add.
    /// Oracle: the old id's termination and tick arrive after the re-add.
    /// </remarks>
    [Fact]
    public void RemovedTopicIgnoresStaleEvents()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        var oldId = harness.Ids()[Aaa];

        harness.Subscription.Remove(Aaa);
        Assert.Equal(oldId, harness.Session.UnsubscribeCalls[^1][0].CorrelationID);
        harness.Subscription.Add(Aaa);
        Assert.NotEqual(oldId, harness.Ids()[Aaa]);
        harness.Deliver(
            Status(Failed(oldId, "gone", "SubscriptionTerminated")),
            Data((oldId, "<LAST_PRICE>1.0</LAST_PRICE>")));

        var state = harness.Subscription.GetStatus().Topics[Aaa];
        Assert.Equal(TopicStatus.Pending, state.Status);
        Assert.Null(state.FirstDataAt);
        Assert.Empty(harness.Ticks);
    }

    /// <summary>
    /// SubscriptionStarted marks a topic subscribed, and SubscriptionFailure marks it
    /// failed with Bloomberg's description.
    /// </summary>
    /// <remarks>
    /// Mutation: a message type matched in lower camel case, or the description read
    /// off the message root.
    /// Oracle: SDK admin events for two topics.
    /// </remarks>
    [Fact]
    public void StatusEventsMarkSubscribedAndFailed()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa, Bbb);
        var ids = harness.Ids();

        harness.Deliver(Status(Started(ids[Aaa]), Failed(ids[Bbb], "bad")));

        var topics = harness.Subscription.GetStatus().Topics;
        Assert.Equal(TopicStatus.Subscribed, topics[Aaa].Status);
        Assert.NotNull(topics[Aaa].SubscribedAt);
        Assert.Equal((TopicStatus.Failed, "bad"), (topics[Bbb].Status, topics[Bbb].Reason));
    }

    /// <summary>Add subscribes a failed topic again under a new id.</summary>
    /// <remarks>
    /// Mutation: Add skipping every held topic.
    /// Oracle: the failed topic appears in a second Subscribe call.
    /// </remarks>
    [Fact]
    public void AddResubscribesFailedTopic()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        var id = harness.Ids()[Aaa];
        harness.Deliver(Status(Failed(id, "bad")));

        harness.Subscription.Add(Aaa);

        Assert.Equal(2, harness.Session.SubscribeCalls.Count);
        Assert.NotEqual(id, harness.Ids()[Aaa]);
        Assert.Equal(TopicStatus.Pending, harness.Subscription.GetStatus().Topics[Aaa].Status);
    }

    /// <summary>The callback gets the topic and the requested fields present.</summary>
    /// <remarks>
    /// Mutation: rows built from every requested field, or keyed by correlation id.
    /// Oracle: LAST_PRICE 101.5 set by hand; BID absent.
    /// </remarks>
    [Fact]
    public void TickRowHoldsPresentFields()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);

        harness.Deliver(Data((harness.Ids()[Aaa], "<LAST_PRICE>101.5</LAST_PRICE>")));

        var (topic, row) = Assert.Single(harness.Ticks);
        Assert.Equal(Aaa, topic);
        Assert.Equal(new Dictionary<string, object?> { ["LAST_PRICE"] = 101.5 }, row);
        var state = harness.Subscription.GetStatus().Topics[Aaa];
        Assert.NotNull(state.FirstDataAt);
        Assert.True(state.LastTickAt >= state.FirstDataAt);
    }

    /// <summary>
    /// TicksLastMinute counts ticks under 60 seconds old at the read.
    /// </summary>
    /// <remarks>
    /// Mutation: ticks dropped only when a new tick arrives, or a window that keeps a
    /// tick exactly 60 seconds old.
    /// Oracle: ticks at 0 s and 30 s; reads at 59.9, 60 and 90 s.
    /// </remarks>
    [Fact]
    public void TicksLastMinuteCountsWindowAtRead()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        var id = harness.Ids()[Aaa];
        foreach (double at in (double[])[0, 30])
        {
            harness.At(at);
            harness.Deliver(Data((id, "<LAST_PRICE>1.0</LAST_PRICE>")));
        }

        List<int> counts = [];
        foreach (double at in (double[])[59.9, 60, 90])
        {
            harness.At(at);
            counts.Add(harness.Subscription.GetStatus().Topics[Aaa].TicksLastMinute);
        }

        Assert.Equal([2, 1, 0], counts);
    }

    /// <summary>
    /// A callback exception is counted and the event's next message still arrives.
    /// </summary>
    /// <remarks>
    /// Mutation: one catch around the whole event, or errors left uncounted.
    /// Oracle: one event with ticks for two topics; the callback throws on the first.
    /// </remarks>
    [Fact]
    public void CallbackErrorKeepsRestOfEvent()
    {
        List<string> seen = [];
        var harness = new SubscriptionHarness((topic, _) =>
        {
            seen.Add(topic);
            if (topic == Aaa) throw new InvalidOperationException("boom");
        });
        harness.Subscription.Add(Aaa, Bbb);
        var ids = harness.Ids();

        harness.Deliver(Data(
            (ids[Aaa], "<LAST_PRICE>1.0</LAST_PRICE>"),
            (ids[Bbb], "<LAST_PRICE>2.0</LAST_PRICE>")));

        var status = harness.Subscription.GetStatus();
        Assert.Equal([Aaa, Bbb], seen);
        Assert.Equal(1, status.ErrorCount);
        Assert.Equal("AAA US Equity: InvalidOperationException: boom", status.LastError);
    }

    /// <summary>
    /// A parse exception is counted and the next message still ticks.
    /// </summary>
    /// <remarks>
    /// Mutation: the per-message catch placed around the callback alone.
    /// Oracle: the first message holds BID, a choice the parser cannot read; the
    /// second holds LAST_PRICE 20.0.
    /// </remarks>
    [Fact]
    public void ParseErrorKeepsRestOfEvent()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa, Bbb);
        var ids = harness.Ids();

        harness.Deliver(Data(
            (ids[Aaa], "<BID><price>1.0</price></BID>"),
            (ids[Bbb], "<LAST_PRICE>20.0</LAST_PRICE>")));

        var (topic, row) = Assert.Single(harness.Ticks);
        Assert.Equal((Bbb, 20.0), (topic, row["LAST_PRICE"]));
        Assert.StartsWith("AAA US Equity: ", harness.Subscription.GetStatus().LastError);
    }

    /// <summary>The callback runs with no lock held.</summary>
    /// <remarks>
    /// Mutation: the callback run inside the state lock, which deadlocks here.
    /// Oracle: the delivery thread finishes, and GetStatus saw the tick.
    /// </remarks>
    [Fact]
    public void CallbackMayCallBackIntoSubscription()
    {
        List<DateTimeOffset?> seen = [];
        SubscriptionHarness harness = null!;
        harness = new SubscriptionHarness((topic, _) =>
            seen.Add(harness.Subscription.GetStatus().Topics[topic].LastTickAt));
        harness.Subscription.Add(Aaa);

        harness.Deliver(Data((harness.Ids()[Aaa], "<LAST_PRICE>1.0</LAST_PRICE>")));

        Assert.NotNull(Assert.Single(seen));
    }

    /// <summary>SessionTerminated outside Stop sets the failure.</summary>
    /// <remarks>
    /// Mutation: the message type matched in lower camel case.
    /// Oracle: an SDK SessionTerminated event after data.
    /// </remarks>
    [Fact]
    public void SessionTerminatedOutsideStopIsFailure()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        harness.Deliver(Data((harness.Ids()[Aaa], "<LAST_PRICE>1.0</LAST_PRICE>")));

        harness.Deliver(Session("SessionTerminated"));

        Assert.Equal("session terminated", harness.Subscription.GetStatus().Failure);
    }

    /// <summary>
    /// SessionConnectionDown clears Connected and sets no failure, and
    /// SessionConnectionUp restores it.
    /// </summary>
    /// <remarks>
    /// Mutation: a lost connection taken as session death, or Connected never restored.
    /// Oracle: SDK down and up events, read between them.
    /// </remarks>
    [Fact]
    public void ConnectionDownIsNotFailure()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        harness.Deliver(Data((harness.Ids()[Aaa], "<LAST_PRICE>1.0</LAST_PRICE>")));

        harness.Deliver(Session("SessionConnectionDown"));
        var down = harness.Subscription.GetStatus();
        harness.Deliver(Session("SessionConnectionUp"));

        Assert.Equal((false, null), (down.Connected, down.Failure));
        Assert.True(harness.Subscription.GetStatus().Connected);
    }

    /// <summary>
    /// Every held topic failed is a failure naming the first topic's reason, and some
    /// failed is not.
    /// </summary>
    /// <remarks>
    /// Mutation: a failure on the first dead topic, or an off-by-one in the count.
    /// Oracle: two topics fail one after the other, read between.
    /// </remarks>
    [Fact]
    public void AllTopicsFailedNamesFirstReason()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa, Bbb);
        var ids = harness.Ids();

        harness.Deliver(Status(Failed(ids[Aaa], "first")));
        string? partial = harness.Subscription.GetStatus().Failure;
        harness.Deliver(Status(Failed(ids[Bbb], "second", "SubscriptionTerminated")));

        Assert.Null(partial);
        Assert.Equal("all 2 topics failed: first", harness.Subscription.GetStatus().Failure);
    }

    /// <summary>The all-failed rule holds over the topics held at the read.</summary>
    /// <remarks>
    /// Mutation: the rule judged only when a status event arrives.
    /// Oracle: one topic failed, one ticking; the ticking one is removed.
    /// </remarks>
    [Fact]
    public void RemovingGoodTopicLeavesFailedOnesAsFailure()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa, Bbb);
        var ids = harness.Ids();
        harness.Deliver(
            Status(Failed(ids[Aaa], "bad")), Data((ids[Bbb], "<LAST_PRICE>1.0</LAST_PRICE>")));

        harness.Subscription.Remove(Bbb);

        Assert.Equal("all 1 topics failed: bad", harness.Subscription.GetStatus().Failure);
    }

    /// <summary>
    /// A failure lacking its optional description still lets the rest of the event's
    /// topics count.
    /// </summary>
    /// <remarks>
    /// Mutation: the description read unguarded, whose exception skips the event's
    /// second message.
    /// Oracle: one SDK event holds both failures, the first with an empty reason.
    /// </remarks>
    [Fact]
    public void FailureWithoutDescriptionCountsEveryTopic()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa, Bbb);
        var ids = harness.Ids();

        harness.Deliver(Status(Failed(ids[Aaa], null), Failed(ids[Bbb], "bad")));

        Assert.Equal(
            "all 2 topics failed: no description", harness.Subscription.GetStatus().Failure);
    }

    /// <summary>No tick within 60 seconds of the first add is a failure.</summary>
    /// <remarks>
    /// Mutation: the no-data rule dropped, or a limit other than 60 seconds.
    /// Oracle: reads at 59.99 s and 60 s after an add at 0 s.
    /// </remarks>
    [Fact]
    public void NoDataWithin60SecondsIsFailure()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);

        harness.At(59.99);
        string? before = harness.Subscription.GetStatus().Failure;
        harness.At(60);

        Assert.Null(before);
        Assert.Equal(
            "no subscription data within 60 s", harness.Subscription.GetStatus().Failure);
    }

    /// <summary>
    /// The no-data rule judges the first tick's time, whenever it is read.
    /// </summary>
    /// <remarks>
    /// Mutation: the rule judged against the read time, or first data disarming it
    /// outright.
    /// Oracle: a tick one second either side of the limit, read at 120 s.
    /// </remarks>
    /// <param name="dataAt">Seconds after the add when the first tick arrives.</param>
    /// <param name="failure">The expected failure.</param>
    [Theory]
    [InlineData(59, null)]
    [InlineData(60, "no subscription data within 60 s")]
    public void NoDataRuleIgnoresReadTime(double dataAt, string? failure)
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);

        harness.At(dataAt);
        harness.Deliver(Data((harness.Ids()[Aaa], "<LAST_PRICE>1.0</LAST_PRICE>")));
        harness.At(120);

        Assert.Equal(failure, harness.Subscription.GetStatus().Failure);
    }

    /// <summary>A failed subscribe leaves none of the call's topics held.</summary>
    /// <remarks>
    /// Mutation: the rollback skipped, or the SDK error thrown unmapped.
    /// Oracle: the exception type the SDK throws on a session that is not started; a
    /// later add works.
    /// </remarks>
    [Fact]
    public void AddRollsBackWhenSessionNotStarted()
    {
        var harness = new SubscriptionHarness();
        harness.Session.SubscribeErrors.Enqueue(NotStarted());

        Assert.Throws<SessionNotAvailableException>(() => harness.Subscription.Add(Aaa));
        Assert.Empty(harness.Subscription.GetStatus().Topics);
        harness.Subscription.Add(Aaa);

        Assert.Equal([Aaa], harness.Subscription.GetStatus().Topics.Keys);
    }

    /// <summary>
    /// Stop unsubscribes, waits for SessionTerminated, releases the session once, and
    /// reads as no failure.
    /// </summary>
    /// <remarks>
    /// Mutation: the session released before it terminates, a clean stop read as a
    /// failure, or a second Stop releasing again.
    /// Oracle: BeginStop delivers SessionTerminated on another thread.
    /// </remarks>
    [Fact]
    public void StopWaitsForTerminationThenReleases()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        var id = harness.Ids()[Aaa];
        harness.Deliver(Data((id, "<LAST_PRICE>1.0</LAST_PRICE>")));
        harness.Session.OnBeginStop = () => TerminateOnNewThread(harness);

        bool first = harness.Subscription.Stop(_wait);
        bool second = harness.Subscription.Stop(_wait);

        Assert.Equal((true, true), (first, second));
        Assert.Equal(id, harness.Session.UnsubscribeCalls[^1][0].CorrelationID);
        Assert.Equal(1, harness.Session.DisposeCount);
        Assert.Null(harness.Subscription.GetStatus().Failure);
    }

    /// <summary>
    /// A Stop that times out keeps the session, and a later Stop releases it.
    /// </summary>
    /// <remarks>
    /// Mutation: the session released on a timeout, or a second Stop returning at once.
    /// Oracle: BeginStop delivers nothing; SessionTerminated comes later.
    /// </remarks>
    [Fact]
    public void StopTimeoutKeepsSessionAndRetryReleases()
    {
        var harness = new SubscriptionHarness();

        bool timedOut = harness.Subscription.Stop(TimeSpan.Zero);
        Assert.Equal(0, harness.Session.DisposeCount);
        harness.Deliver(Session("SessionTerminated"));
        bool retried = harness.Subscription.Stop(TimeSpan.Zero);

        Assert.Equal((false, true), (timedOut, retried));
        Assert.Equal(1, harness.Session.DisposeCount);
    }

    /// <summary>
    /// Stop called from the callback begins the stop and returns, and a later Stop
    /// from the owner releases the session.
    /// </summary>
    /// <remarks>
    /// Mutation: the dispatch-thread check dropped, which waits out the timeout on the
    /// thread that must deliver SessionTerminated.
    /// Oracle: a 60 s timeout against the harness's 5 s hang check.
    /// </remarks>
    [Fact]
    public void StopFromCallbackReturnsWithoutWaiting()
    {
        SubscriptionHarness harness = null!;
        harness = new SubscriptionHarness(
            (_, _) => harness.Subscription.Stop(TimeSpan.FromSeconds(60)));
        harness.Subscription.Add(Aaa);

        harness.Deliver(Data((harness.Ids()[Aaa], "<LAST_PRICE>1.0</LAST_PRICE>")));
        Assert.Equal(1, harness.Session.BeginStopCount);
        Assert.Equal(0, harness.Session.DisposeCount);
        harness.Deliver(Session("SessionTerminated"));

        Assert.True(harness.Subscription.Stop(TimeSpan.Zero));
        Assert.Equal(1, harness.Session.DisposeCount);
    }

    /// <summary>
    /// Stop still stops and releases the session when Unsubscribe throws.
    /// </summary>
    /// <remarks>
    /// Mutation: the SDK error left to escape Stop, or BeginStop in the same try as
    /// Unsubscribe.
    /// Oracle: BeginStop delivers SessionTerminated; nothing else does.
    /// </remarks>
    [Fact]
    public void StopReleasesWhenUnsubscribeThrows()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        harness.Session.UnsubscribeError = new ArgumentException("bad");
        harness.Session.OnBeginStop = () => TerminateOnNewThread(harness);

        Assert.True(harness.Subscription.Stop(_wait));

        Assert.Equal(1, harness.Session.DisposeCount);
        Assert.Equal(1, harness.Subscription.GetStatus().ErrorCount);
    }

    /// <summary>Add refuses once Stop has begun.</summary>
    /// <remarks>
    /// Mutation: the stopping check dropped from Add.
    /// Oracle: Stop with no termination, then Add.
    /// </remarks>
    [Fact]
    public void AddAfterStopThrows()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Stop(TimeSpan.Zero);

        var error = Assert.Throws<SessionException>(() => harness.Subscription.Add(Aaa));

        Assert.Contains("stopped", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Session.SubscribeAttempts);
    }

    /// <summary>The session is disposed when //blp/mktdata fails to open.</summary>
    /// <remarks>
    /// Mutation: OpenService called outside the try.
    /// Oracle: a recording session whose OpenService throws counts one dispose.
    /// </remarks>
    [Fact]
    public void FailedOpenServiceDisposesSession()
    {
        var session = new RecordingSession
        {
            OpenServiceError = new SessionStartException("no service"),
        };

        Assert.Throws<SessionStartException>(() => new SubscriptionHarness(session: session));

        Assert.Equal(1, session.DisposeCount);
    }

    /// <summary>A stopped subscription reads no failure later.</summary>
    /// <remarks>
    /// Mutation: the no-data or all-failed rule applied after Stop.
    /// Oracle: add at 0 s with no tick, stop at 5 s, read at 61 s; then the good topic
    /// removed, leaving only the failed one.
    /// </remarks>
    [Fact]
    public void CleanStopNeverReadsAsFailure()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa, Bbb);
        harness.Deliver(Status(Failed(harness.Ids()[Aaa], "bad")));
        harness.At(5);
        harness.Session.OnBeginStop = () => TerminateOnNewThread(harness);
        harness.Subscription.Stop(_wait);

        harness.At(61);
        string? late = harness.Subscription.GetStatus().Failure;
        harness.Subscription.Remove(Bbb);

        Assert.Null(late);
        Assert.Null(harness.Subscription.GetStatus().Failure);
    }

    /// <summary>
    /// Stop releases the session when called on the thread that delivered
    /// SessionTerminated.
    /// </summary>
    /// <remarks>
    /// Mutation: the dispatch thread's id kept after SessionTerminated.
    /// Oracle: termination delivered on the test thread, then Stop there.
    /// </remarks>
    [Fact]
    public void StopOnThreadThatDeliveredTerminationReleases()
    {
        var harness = new SubscriptionHarness();

        harness.Handle(Session("SessionTerminated"));

        Assert.True(harness.Subscription.Stop(TimeSpan.Zero));
        Assert.Equal(1, harness.Session.DisposeCount);
    }

    /// <summary>A failed re-add of a failed topic restores its failed state.</summary>
    /// <remarks>
    /// Mutation: the rollback deleting the topic's prior state.
    /// Oracle: the only topic failed; the re-add's Subscribe throws.
    /// </remarks>
    [Fact]
    public void FailedReaddRollbackKeepsFailure()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        var id = harness.Ids()[Aaa];
        harness.Deliver(Status(Failed(id, "bad")));
        harness.Session.SubscribeErrors.Enqueue(NotStarted());

        Assert.Throws<SessionNotAvailableException>(() => harness.Subscription.Add(Aaa));
        Assert.Equal("all 1 topics failed: bad", harness.Subscription.GetStatus().Failure);
        harness.Deliver(Status(Failed(id, "again")));

        Assert.Equal("again", harness.Subscription.GetStatus().Topics[Aaa].Reason);
    }

    /// <summary>Each topic is subscribed with the fields and the interval.</summary>
    /// <remarks>
    /// Mutation: the fields or the interval option dropped from the subscription.
    /// Oracle: the SDK's own topic string for a 2 s interval.
    /// </remarks>
    [Fact]
    public void SubscribeSendsFieldsAndInterval()
    {
        var harness = new SubscriptionHarness(
            options: new SubscriptionOptions { Interval = TimeSpan.FromSeconds(2) });

        harness.Subscription.Add(Aaa);

        Assert.Equal(
            "AAA US Equity?fields=LAST_PRICE,BID&interval=2.0",
            harness.Session.SubscribeCalls[^1][0].SubscriptionString);
    }

    /// <summary>A topic named twice in one Add is subscribed once.</summary>
    /// <remarks>
    /// Mutation: the Distinct dropped from Add.
    /// Oracle: one call naming the same topic twice.
    /// </remarks>
    [Fact]
    public void AddDedupsTopicsInOneCall()
    {
        var harness = new SubscriptionHarness();

        harness.Subscription.Add(Aaa, Aaa);

        Assert.Single(harness.Session.SubscribeCalls[^1]);
    }

    /// <summary>
    /// The no-data clock starts at the first add that subscribed and never restarts.
    /// </summary>
    /// <remarks>
    /// Mutation: the clock set before Subscribe succeeds, or reset on each add.
    /// Oracle: a failed add at 0 s, a good add at 30 s, another at 60 s.
    /// </remarks>
    [Fact]
    public void NoDataClockStartsAtFirstGoodAdd()
    {
        var harness = new SubscriptionHarness();
        harness.Session.SubscribeErrors.Enqueue(NotStarted());

        Assert.Throws<SessionNotAvailableException>(() => harness.Subscription.Add(Aaa));
        harness.At(30);
        harness.Subscription.Add(Aaa);
        harness.At(60);
        harness.Subscription.Add(Bbb);
        harness.At(89.9);
        string? early = harness.Subscription.GetStatus().Failure;
        harness.At(90);

        Assert.Null(early);
        Assert.Equal(
            "no subscription data within 60 s", harness.Subscription.GetStatus().Failure);
    }

    /// <summary>Later ticks keep both first-data times.</summary>
    /// <remarks>
    /// Mutation: FirstDataAt, or the subscription's first tick time, overwritten.
    /// Oracle: ticks at 10 s and 70 s, read at 120 s.
    /// </remarks>
    [Fact]
    public void FirstDataTimesAreKept()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        var id = harness.Ids()[Aaa];
        harness.At(10);
        harness.Deliver(Data((id, "<LAST_PRICE>1.0</LAST_PRICE>")));
        var first = harness.Subscription.GetStatus().Topics[Aaa].FirstDataAt;

        harness.At(70);
        harness.Deliver(Data((id, "<LAST_PRICE>2.0</LAST_PRICE>")));
        harness.At(120);

        var status = harness.Subscription.GetStatus();
        Assert.Null(status.Failure);
        Assert.Equal(first, status.Topics[Aaa].FirstDataAt);
        Assert.True(status.Topics[Aaa].LastTickAt > first);
    }

    /// <summary>Session termination is the failure reported over all-failed.</summary>
    /// <remarks>
    /// Mutation: the failure rules checked in another order.
    /// Oracle: the only topic fails, then the session terminates.
    /// </remarks>
    [Fact]
    public void SessionTerminatedOutranksAllFailed()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);

        harness.Deliver(Status(Failed(harness.Ids()[Aaa], "bad")), Session("SessionTerminated"));

        Assert.Equal("session terminated", harness.Subscription.GetStatus().Failure);
    }

    /// <summary>Stop ignores later ticks and stops a session with no topics.</summary>
    /// <remarks>
    /// Mutation: the id map kept in Stop, or BeginStop skipped when nothing is held.
    /// Oracle: a tick after Stop; an empty subscription stopped.
    /// </remarks>
    [Fact]
    public void StopDropsLaterTicksAndStopsEmptySession()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        var id = harness.Ids()[Aaa];
        harness.Subscription.Remove(Aaa);

        harness.Subscription.Stop(TimeSpan.Zero);
        harness.Deliver(Data((id, "<LAST_PRICE>1.0</LAST_PRICE>")));

        Assert.Empty(harness.Ticks);
        Assert.Equal(1, harness.Session.BeginStopCount);
    }

    /// <summary>Stop ignores later ticks of a topic it still held.</summary>
    /// <remarks>
    /// Mutation: the id map kept in Stop.
    /// Oracle: a tick for a held topic after Stop.
    /// </remarks>
    [Fact]
    public void StopDropsLaterTicksOfHeldTopics()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);

        harness.Subscription.Stop(TimeSpan.Zero);
        harness.Deliver(Data((harness.Ids()[Aaa], "<LAST_PRICE>1.0</LAST_PRICE>")));

        Assert.Empty(harness.Ticks);
    }

    /// <summary>A GetStatus result does not change after later events.</summary>
    /// <remarks>
    /// Mutation: GetStatus returning the live topic states.
    /// Oracle: a held copy read after SubscriptionStarted.
    /// </remarks>
    [Fact]
    public void StatusTopicsAreCopies()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        var before = harness.Subscription.GetStatus().Topics;

        harness.Deliver(Status(Started(harness.Ids()[Aaa])));

        Assert.Equal(TopicStatus.Pending, before[Aaa].Status);
    }

    /// <summary>
    /// Remove throws SessionNotAvailableException on a dead session and drops the
    /// topics anyway.
    /// </summary>
    /// <remarks>
    /// Mutation: the SDK error thrown unmapped, or the topics kept on error.
    /// Oracle: the exception type the SDK throws on a session that is not started.
    /// </remarks>
    [Fact]
    public void RemoveMapsNotStartedError()
    {
        var harness = new SubscriptionHarness();
        harness.Subscription.Add(Aaa);
        harness.Session.UnsubscribeError = NotStarted();

        Assert.Throws<SessionNotAvailableException>(() => harness.Subscription.Remove(Aaa));

        Assert.Empty(harness.Subscription.GetStatus().Topics);
    }

    /// <summary>A topic the SDK rejects leaves no topic of the call held.</summary>
    /// <remarks>
    /// Mutation: a subscription built inside the loop that updates the state, which
    /// leaves the good topic held but never subscribed.
    /// Oracle: the SDK's own check, which rejects a topic holding a question mark; a
    /// later add of the good topic subscribes it.
    /// </remarks>
    [Fact]
    public void RejectedTopicLeavesNoTopicHeld()
    {
        var harness = new SubscriptionHarness();

        Assert.ThrowsAny<ArgumentException>(() => harness.Subscription.Add(Aaa, "AAA US Equity?"));
        Assert.Empty(harness.Subscription.GetStatus().Topics);
        harness.Subscription.Add(Aaa);

        Assert.Single(harness.Session.SubscribeCalls);
        Assert.Equal([Aaa], harness.Subscription.GetStatus().Topics.Keys);
    }

    /// <summary>A negative timeout throws before the stop begins.</summary>
    /// <remarks>
    /// Mutation: the timeout checked only by the wait, after the session is stopping.
    /// Oracle: an add after the throw, which a begun stop rejects.
    /// </remarks>
    [Fact]
    public void NegativeStopTimeoutThrowsBeforeStopping()
    {
        var harness = new SubscriptionHarness();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => harness.Subscription.Stop(TimeSpan.FromSeconds(-2)));
        harness.Subscription.Add(Aaa);

        Assert.Equal(0, harness.Session.BeginStopCount);
        Assert.Equal([Aaa], harness.Subscription.GetStatus().Topics.Keys);
    }

    private static InvalidOperationException NotStarted() => new("Session Not Started");

    private static void TerminateOnNewThread(SubscriptionHarness harness) =>
        new Thread(() => harness.Handle(Session("SessionTerminated"))).Start();
}
