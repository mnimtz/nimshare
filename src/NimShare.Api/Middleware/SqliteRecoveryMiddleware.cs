using Microsoft.Data.Sqlite;

namespace NimShare.Api.Middleware;

/// <summary>
/// Recovers from transient Sqlite failures — specifically the
/// SQLITE_CANTOPEN / SQLITE_BUSY / SQLITE_READONLY class you get when Azure
/// Files remounts the /data share out from under a running app.
///
/// Once the underlying file blinks, every pooled SqliteConnection in the app
/// holds a stale handle: the file descriptor points at nothing, and every
/// subsequent query throws error 14 for the rest of the process's life. The
/// user then sees the friendly 503 forever, even after the mount comes back.
///
/// This middleware clears the connection pool on the FIRST such failure per
/// request and retries once. If the mount is back, the retry succeeds and the
/// user sees a normal page. If not, the exception flows on to /error, where
/// the browser refreshes every 15 s. Either way the app self-heals as soon
/// as Azure Files reappears — no process restart required.
/// </summary>
public class SqliteRecoveryMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SqliteRecoveryMiddleware> _log;

    public SqliteRecoveryMiddleware(RequestDelegate next, ILogger<SqliteRecoveryMiddleware> log)
    {
        _next = next; _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (Exception ex) when (IsTransientSqlite(ex))
        {
            // Reset every pooled connection so the NEXT request opens a fresh
            // fd against whatever /data/nimshare.db currently is. This is safe
            // and self-healing regardless of retry.
            SqliteConnection.ClearAllPools();

            // Only retry idempotent HTTP methods. Retrying a POST would replay
            // an already-consumed request body (empty on the retry ⇒ 400 or
            // silent no-op) and could double-write anything a half-succeeded
            // SaveChanges already committed. GET/HEAD/OPTIONS are safe with
            // ONE more exception: side-effectful GETs like /sign/{pid}
            // (records ViewedAt + inserts an audit row) can double-insert if
            // the first attempt half-succeeded — skip those too.
            var m = ctx.Request.Method;
            var path = ctx.Request.Path.Value ?? "";
            var sideEffectingGet = path.StartsWith("/sign/", StringComparison.OrdinalIgnoreCase);
            var safe = (HttpMethods.IsGet(m) || HttpMethods.IsHead(m) || HttpMethods.IsOptions(m))
                       && !sideEffectingGet;
            if (!safe || ctx.Response.HasStarted)
            {
                _log.LogWarning(ex, "Sqlite transient ({Method}) — pool cleared, surfacing to error handler.", m);
                throw;
            }
            _log.LogInformation("Sqlite transient ({Method}) — cleared pool, retrying once.", m);
            ctx.Response.Clear();
            await Task.Delay(400);
            try
            {
                await _next(ctx);
            }
            catch (Exception retry) when (IsTransientSqlite(retry))
            {
                _log.LogWarning(retry, "Sqlite transient still failing after pool reset — surfacing to error handler.");
                throw;
            }
        }
    }

    private static bool IsTransientSqlite(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is SqliteException sx)
            {
                // 14 CANTOPEN, 5 BUSY, 6 LOCKED, 8 READONLY, 10 IOERR
                if (sx.SqliteErrorCode is 14 or 5 or 6 or 8 or 10) return true;
                var lo = (sx.Message ?? "").ToLowerInvariant();
                if (lo.Contains("unable to open") || lo.Contains("locked") ||
                    lo.Contains("busy") || lo.Contains("disk i/o"))
                    return true;
            }
            ex = ex.InnerException;
        }
        return false;
    }
}
