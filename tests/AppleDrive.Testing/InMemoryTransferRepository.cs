using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;

namespace AppleDrive.Testing;

/// <summary>Thread-safe, list-backed <see cref="ITransferRepository"/>.</summary>
public sealed class InMemoryTransferRepository : ITransferRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TransferSessionRecord> _sessions = [];
    private readonly List<TransferRecord> _transfers = [];

    public IReadOnlyList<TransferRecord> Transfers
    {
        get
        {
            lock (_lock)
            {
                return [.. _transfers];
            }
        }
    }

    public Task CreateSessionAsync(TransferSessionRecord session, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _sessions.Add(session.Id, session);
        }

        return Task.CompletedTask;
    }

    public Task CompleteSessionAsync(TransferSessionRecord session, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _sessions[session.Id] = session;
        }

        return Task.CompletedTask;
    }

    public Task<TransferSessionRecord?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_sessions.GetValueOrDefault(sessionId));
        }
    }

    public Task<long> AddAsync(TransferRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var id = _transfers.Count + 1L;
            _transfers.Add(record with { Id = id });
            return Task.FromResult(id);
        }
    }

    public Task CompleteAsync(long id, TransferOutcomeRecord outcome, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = _transfers.FindIndex(record => record.Id == id);
            _transfers[index] = _transfers[index] with
            {
                Status = outcome.Status,
                CompletedAt = outcome.CompletedAt,
                DestinationPath = outcome.DestinationPath,
                Sha256 = outcome.Sha256,
                FileSize = outcome.FileSize,
                ErrorKind = outcome.ErrorKind,
                ErrorMessage = outcome.ErrorMessage,
            };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransferRecord>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<TransferRecord>>(_transfers.Where(record => record.SessionId == sessionId).ToList());
        }
    }

    public Task SetTargetAsync(long id, string destinationPath, byte[] sha256, long fileSize, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = _transfers.FindIndex(record => record.Id == id);
            _transfers[index] = _transfers[index] with { DestinationPath = destinationPath, Sha256 = sha256, FileSize = fileSize };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransferSessionRecord>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<TransferSessionRecord>>(
                _sessions.Values.OrderByDescending(session => session.StartedAt).Take(limit).ToList());
        }
    }

    public Task<IReadOnlyList<TransferSessionRecord>> GetRunningSessionsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<TransferSessionRecord>>(
                _sessions.Values.Where(session => session.Status == TransferSessionStatus.Running).ToList());
        }
    }

    public Task<IReadOnlyList<TransferRecord>> GetCompletedUnderRootAsync(string root, CancellationToken cancellationToken)
    {
        var prefix = root.EndsWith('\\') ? root : root + '\\';
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<TransferRecord>>(_transfers
                .Where(record => record.Status == TransferStatus.Completed
                    && record.DestinationPath?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                .ToList());
        }
    }

    public Task<IReadOnlyList<TransferRecord>> GetInProgressAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<TransferRecord>>(_transfers.Where(record => record.Status == TransferStatus.InProgress).ToList());
        }
    }
}
