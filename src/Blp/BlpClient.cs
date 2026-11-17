using Bloomberglp.Blpapi;
using Blp.HistoricalData;
using Blp.Internal;
using Blp.ReferenceData;

namespace Blp;

/// <summary>
/// A started session with the local Bloomberg terminal. Requests may run concurrently.
/// </summary>
public sealed class BlpClient : IDisposable
{
    private const string RefDataServiceName = "//blp/refdata";

    private static readonly ReferenceDataRequest _connectivityProbe =
        new(["IBM US Equity"], ["ID_BB_GLOBAL"]);

    private readonly Session _session;
    private readonly RequestDispatcher _dispatcher;
    private readonly Service _refDataService;

    private BlpClient(Session session, RequestDispatcher dispatcher, Service refDataService)
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
    public static async Task<BlpClient> ConnectAsync(
        BlpClientOptions options, CancellationToken cancellationToken = default)
    {
        var dispatcher = new RequestDispatcher(options.IdleTimeout, TimeProvider.System);
        var sessionOptions = new SessionOptions
        {
            ServerHost = "localhost",
            ServerPort = 8194,
            AuthenticationOptions = "AuthenticationType=OS_LOGON",
            MaxEventQueueSize = 10000,
        };
        if (options.ApplicationIdentityKey is { } key) sessionOptions.ApplicationIdentityKey = key;

        var session = new Session(
            sessionOptions,
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
    /// <exception cref="RequestTimeoutException">The idle timeout passed.</exception>
    /// <exception cref="SessionNotAvailableException">
    /// The session is not started, or Bloomberg reported a dead connection or a missing
    /// service.
    /// </exception>
    /// <exception cref="SessionTerminatedException">The session terminated.</exception>
    /// <exception cref="ResponseException">Bloomberg failed the request.</exception>
    public async Task<ReferenceDataResponse> GetReferenceDataAsync(
        ReferenceDataRequest request, CancellationToken cancellationToken = default)
    {
        var handler = new ReferenceDataHandler(request);
        await SendAsync(handler, cancellationToken).ConfigureAwait(false);
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
    /// <exception cref="RequestTimeoutException">The idle timeout passed.</exception>
    /// <exception cref="SessionNotAvailableException">
    /// The session is not started, or Bloomberg reported a dead connection or a missing
    /// service.
    /// </exception>
    /// <exception cref="SessionTerminatedException">The session terminated.</exception>
    /// <exception cref="ResponseException">Bloomberg failed the request.</exception>
    public async Task<HistoricalDataResponse> GetHistoricalDataAsync(
        HistoricalDataRequest request, CancellationToken cancellationToken = default)
    {
        var handler = new HistoricalDataHandler(request);
        await SendAsync(handler, cancellationToken).ConfigureAwait(false);
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

    private async Task SendAsync(IRequestHandler handler, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bloombergRequest = handler.CreateRequest(_refDataService);
        var correlationId = new CorrelationID();
        var response = _dispatcher.Register(correlationId, handler);
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
