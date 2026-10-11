using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiNative.Infrastructure.Knowledge;

/// <summary>
/// 在 EF 的连接与当前事务上执行原生 SQL（向量列不经过 EF 映射）。
/// </summary>
internal static class RawSql
{
    public static async Task<T> RunAsync<T>(DatabaseFacade database, Func<DbCommand, Task<T>> work, CancellationToken cancellationToken)
    {
        var connection = database.GetDbConnection();
        var opened = false;
        if (connection.State != ConnectionState.Open)
        {
            await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            opened = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = database.CurrentTransaction?.GetDbTransaction();
            return await work(command).ConfigureAwait(false);
        }
        finally
        {
            if (opened)
            {
                await database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }
}
