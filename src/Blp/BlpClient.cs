using Bloomberglp.Blpapi;
using Blp.Bql;
using Blp.Eqs;
using Blp.HistoricalData;
using Blp.Internal;
using Blp.IntradayBars;
using Blp.IntradayTicks;
using Blp.ReferenceData;

namespace Blp;

/// <summary>
/// A started session with the local Bloomberg terminal. Requests may run concurrently.
/// </summary>
public sealed class BlpClient : IDisposable
{
    private const string RefDataServiceName = "//blp/refdata";
    private const string BqlServiceName = "//blp/bqlsvc";

    private static readonly ReferenceDataRequest _connectivityProbe =
        new(["IBM US Equity"], ["ID_BB_GLOBAL"]);

    private readonly IBlpSession _session;
    private readonly RequestDispatcher _dispatcher;
    private readonly Service _refDataService;
    private readonly Lock _bqlServiceLock = new();
    private Service? _bqlService;

    private BlpClient(IBlpSession session, RequestDispatcher dispatcher, Service refDataService)
    {
        _session = session;
        _dispatcher = dispatcher;
        _refDataService = refDataService;
    }

    /// <summary>
    /// Starts a session on localhost:8194 with OS logon, and checks it with one
    /// reference data request.
    /// </summary>
    /// <param name="options">Session settings.</param>
    /// <param name="cancellationToken">Cancels the connectivity check.</param>
    /// <returns>The connected client.</returns>
    /// <exception cref="SessionCreateException">
    /// The session failed to start.
    /// </exception>
    /// <exception cref="SessionStartException">
    /// //blp/refdata failed to open.
    /// </exception>
    /// <exception cref="SessionException">
    /// The SDK threw while starting, or the connectivity check failed.
    /// </exception>
    public static Task<BlpClient> ConnectAsync(
        BlpClientOptions options, CancellationToken cancellationToken = default) =>
        ConnectAsync(
            options,
            (sessionOptions, handler) => new SdkSession(new Session(sessionOptions, handler)),
            cancellationToken);

    /// <summary>
    /// Starts a session from sessionFactory, then checks it as the public overload.
    /// </summary>
    /// <param name="options">Session settings.</param>
    /// <param name="sessionFactory">
    /// Creates the session from its options and event handler.
    /// </param>
    /// <param name="cancellationToken">Cancels the connectivity check.</param>
    /// <returns>The connected client.</returns>
    internal static async Task<BlpClient> ConnectAsync(
        BlpClientOptions options,
        Func<SessionOptions, Bloomberglp.Blpapi.EventHandler, IBlpSession> sessionFactory,
        CancellationToken cancellationToken)
    {
        var dispatcher = new RequestDispatcher(
            options.IdleTimeout, options.TotalTimeout, TimeProvider.System);
        var session = sessionFactory(
            SdkSession.CreateOptions(options.ApplicationIdentityKey),
            (sessionEvent, _) => dispatcher.Dispatch(sessionEvent));
        try
        {
            if (!session.Start())
            {
                throw new SessionCreateException("Failed to start the Bloomberg session.");
            }

            if (!session.OpenService(RefDataServiceName))
            {
                throw new SessionStartException($"Failed to open service {RefDataServiceName}.");
            }
        }
        catch (Exception error)
        {
            session.Dispose();
            if (error is SessionException) throw;
            throw new SessionException("Failed to start the Bloomberg session.", error);
        }

        var client = new BlpClient(session, dispatcher, session.GetService(RefDataServiceName));
        try
        {
            var probe = await client
                .GetReferenceDataAsync(_connectivityProbe, cancellationToken)
                .ConfigureAwait(false);
            if (probe.Values.Count == 0)
            {
                throw new InvalidOperationException("The probe request returned no data.");
            }

            return client;
        }
        catch (Exception error)
        {
            client.Dispose();
            if (error is OperationCanceledException) throw;
            throw new SessionException("Connectivity check failed.", error);
        }
    }

    /// <summary>
    /// Sends a reference data request (BDP) and waits for its full response.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">
    /// Abandons the request. A token with a deadline sets a limit on the whole
    /// response.
    /// </param>
    /// <returns>The response.</returns>
    /// <exception cref="RequestTimeoutException">
    /// The idle or total timeout passed.
    /// </exception>
    /// <exception cref="SessionNotAvailableException">
    /// The session is not started, or Bloomberg reported a dead connection or a missing
    /// service.
    /// </exception>
    /// <exception cref="SessionTerminatedException">The session terminated.</exception>
    /// <exception cref="ResponseException">Bloomberg failed the request.</exception>
    /// <exception cref="RequestSecurityException">
    /// RaiseSecurityError is set and the response holds a security error.
    /// </exception>
    /// <exception cref="RequestFieldException">
    /// RaiseFieldError is set and the response holds a field error.
    /// </exception>
    public async Task<ReferenceDataResponse> GetReferenceDataAsync(
        ReferenceDataRequest request, CancellationToken cancellationToken = default)
    {
        var handler = new ReferenceDataHandler(request);
        await SendAsync(
            handler, _refDataService, request.IdleTimeout, request.TotalTimeout, cancellationToken)
            .ConfigureAwait(false);
        return handler.ToResponse();
    }

    /// <summary>
    /// Sends a historical data request (BDH) and waits for its full response.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">
    /// Abandons the request. A token with a deadline sets a limit on the whole
    /// response.
    /// </param>
    /// <returns>The response.</returns>
    /// <exception cref="RequestTimeoutException">
    /// The idle or total timeout passed.
    /// </exception>
    /// <exception cref="SessionNotAvailableException">
    /// The session is not started, or Bloomberg reported a dead connection or a missing
    /// service.
    /// </exception>
    /// <exception cref="SessionTerminatedException">The session terminated.</exception>
    /// <exception cref="ResponseException">Bloomberg failed the request.</exception>
    /// <exception cref="RequestSecurityException">
    /// RaiseSecurityError is set and the response holds a security error.
    /// </exception>
    /// <exception cref="RequestFieldException">
    /// RaiseFieldError is set and the response holds a field error.
    /// </exception>
    public async Task<HistoricalDataResponse> GetHistoricalDataAsync(
        HistoricalDataRequest request, CancellationToken cancellationToken = default)
    {
        var handler = new HistoricalDataHandler(request);
        await SendAsync(
            handler, _refDataService, request.IdleTimeout, request.TotalTimeout, cancellationToken)
            .ConfigureAwait(false);
        return handler.ToResponse();
    }

    /// <summary>
    /// Sends an intraday tick request and waits for its full response.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">
    /// Abandons the request. A token with a deadline sets a limit on the whole
    /// response.
    /// </param>
    /// <returns>
    /// One name-to-value row per tick, in time order. A name one tick lacks holds null.
    /// </returns>
    /// <exception cref="RequestTimeoutException">
    /// The idle or total timeout passed.
    /// </exception>
    /// <exception cref="SessionNotAvailableException">
    /// The session is not started, or Bloomberg reported a dead connection or a missing
    /// service.
    /// </exception>
    /// <exception cref="SessionTerminatedException">The session terminated.</exception>
    /// <exception cref="ResponseException">Bloomberg failed the request.</exception>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GetIntradayTicksAsync(
        IntradayTickRequest request, CancellationToken cancellationToken = default)
    {
        var handler = new IntradayTickHandler(request);
        await SendAsync(
            handler, _refDataService, request.IdleTimeout, request.TotalTimeout, cancellationToken)
            .ConfigureAwait(false);
        return handler.ToResponse();
    }

    /// <summary>
    /// Sends an intraday bar request and waits for its full response.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">
    /// Abandons the request. A token with a deadline sets a limit on the whole
    /// response.
    /// </param>
    /// <returns>One name-to-value row per bar, in time order.</returns>
    /// <exception cref="RequestTimeoutException">
    /// The idle or total timeout passed.
    /// </exception>
    /// <exception cref="SessionNotAvailableException">
    /// The session is not started, or Bloomberg reported a dead connection or a missing
    /// service.
    /// </exception>
    /// <exception cref="SessionTerminatedException">The session terminated.</exception>
    /// <exception cref="ResponseException">Bloomberg failed the request.</exception>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GetIntradayBarsAsync(
        IntradayBarRequest request, CancellationToken cancellationToken = default)
    {
        var handler = new IntradayBarHandler(request);
        await SendAsync(
            handler, _refDataService, request.IdleTimeout, request.TotalTimeout, cancellationToken)
            .ConfigureAwait(false);
        return handler.ToResponse();
    }

    /// <summary>
    /// Runs a saved equity screen (EQS) and waits for its full response.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">
    /// Abandons the request. A token with a deadline sets a limit on the whole
    /// response.
    /// </param>
    /// <returns>The response.</returns>
    /// <exception cref="RequestTimeoutException">
    /// The idle or total timeout passed.
    /// </exception>
    /// <exception cref="SessionNotAvailableException">
    /// The session is not started, or Bloomberg reported a dead connection or a missing
    /// service.
    /// </exception>
    /// <exception cref="SessionTerminatedException">The session terminated.</exception>
    /// <exception cref="ResponseException">
    /// Bloomberg failed the request, as for an unknown screen.
    /// </exception>
    public async Task<EqsResponse> GetScreenerAsync(
        EqsRequest request, CancellationToken cancellationToken = default)
    {
        var handler = new EqsHandler(request);
        await SendAsync(
            handler, _refDataService, request.IdleTimeout, request.TotalTimeout, cancellationToken)
            .ConfigureAwait(false);
        return handler.ToResponse();
    }

    /// <summary>
    /// Sends a BQL query and waits for its full response. The first call opens
    /// //blp/bqlsvc.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">
    /// Abandons the request. A token with a deadline sets a limit on the whole
    /// response.
    /// </param>
    /// <returns>The response.</returns>
    /// <exception cref="RequestTimeoutException">
    /// The idle or total timeout passed.
    /// </exception>
    /// <exception cref="SessionNotAvailableException">
    /// The session is not started, or Bloomberg reported a dead connection or a missing
    /// service.
    /// </exception>
    /// <exception cref="SessionTerminatedException">The session terminated.</exception>
    /// <exception cref="SessionStartException">
    /// //blp/bqlsvc failed to open. The next call tries again.
    /// </exception>
    /// <exception cref="ResponseException">Bloomberg failed the request.</exception>
    public async Task<BqlResponse> GetBqlAsync(
        BqlRequest request, CancellationToken cancellationToken = default)
    {
        var handler = new BqlHandler(request);
        await SendAsync(
            handler, BqlService(), request.IdleTimeout, request.TotalTimeout, cancellationToken)
            .ConfigureAwait(false);
        return handler.ToResponse();
    }

    /// <summary>Stops the session and fails every pending request.</summary>
    public void Dispose()
    {
        // Stop first: after it returns no event arrives, so a request sent before it
        // cannot wait out the idle timeout.
        _session.Stop();
        _dispatcher.FailAll(new ObjectDisposedException(nameof(BlpClient)));
        _session.Dispose();
    }

    private Service BqlService()
    {
        lock (_bqlServiceLock)
        {
            if (_bqlService is null)
            {
                if (!_session.OpenService(BqlServiceName))
                {
                    throw new SessionStartException($"Failed to open service {BqlServiceName}.");
                }

                _bqlService = _session.GetService(BqlServiceName);
            }

            return _bqlService;
        }
    }

    private async Task SendAsync(
        IRequestHandler handler,
        Service service,
        TimeSpan? idleTimeout,
        TimeSpan? totalTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bloombergRequest = handler.CreateRequest(service);
        var correlationId = new CorrelationID();
        var response = _dispatcher.Register(correlationId, handler, idleTimeout, totalTimeout);
        try
        {
            _session.SendRequest(bloombergRequest, correlationId);
        }
        catch (InvalidOperationException error)
        {
            var notAvailable = new SessionNotAvailableException(error.Message, error);
            _dispatcher.Fail(correlationId, notAvailable);
            throw notAvailable;
        }
        catch (Exception error)
        {
            _dispatcher.Fail(correlationId, error);
            throw;
        }

        var canceled = new OperationCanceledException(cancellationToken);
        using var registration = cancellationToken.Register(
            () => _dispatcher.Fail(correlationId, canceled));
        await response.ConfigureAwait(false);
    }
}
