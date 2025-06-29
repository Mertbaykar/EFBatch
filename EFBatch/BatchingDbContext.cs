
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;

namespace EFBatch
{
    public abstract class BatchingDbContext<TContext> : DbContext where TContext : BatchingDbContext<TContext>
    {

        internal BatchManager BatchManager = new();

        protected BatchingDbContext(DbContextOptions options) : base(options)
        {
            BatchManager = new BatchManager();
        }

        public void BatchUpdate<T>(Func<TContext, IQueryable<T>> sourceQueryFunc,
            Expression<Func<SetPropertyCalls<T>, SetPropertyCalls<T>>> setPropertyCalls) where T : class
        {
            try
            {
                BatchManager.StartBatching();
                sourceQueryFunc((TContext)this).ExecuteUpdate(setPropertyCalls);
            }
            catch (Exception ex)
            {
                throw;
            }
            finally
            {
                BatchManager.StopBatching();
            }

        }
        public void BatchDelete<T>(Func<TContext, IQueryable<T>> sourceQueryFunc) where T : class
        {
            try
            {
                BatchManager.StartBatching();
                sourceQueryFunc((TContext)this).ExecuteDelete();
            }
            catch (Exception ex)
            {
                throw;
            }
            finally
            {
                BatchManager.StopBatching();
            }
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.AddInterceptors(BatchManager.ImmediateCommandInterceptor, BatchManager.SaveInterceptor);
        }

        private DbCommand? PrepareBatchCommand()
        {
            var commands = BatchManager.GetCommands();
            if (commands.Any())
            {
                DbConnection connection = this.Database.GetDbConnection();
                DbCommand finalDbCommand = connection.MergeCommands(commands);
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    connection.Open();
                }
                return finalDbCommand;
            }
            return null;
        }

        public override int SaveChanges()
        {
            var batchCommand = PrepareBatchCommand();
            if (batchCommand != null)
            {
                // external transaction varsa o set ediliyor. Yoksa biz açıyoruz ve base.savechanges'de otomatik olarak kullanılıyor.
                DbTransaction? transaction = Database.CurrentTransaction?.GetDbTransaction();

                if (transaction == null)
                {
                    transaction = Database.BeginTransaction().GetDbTransaction();
                }

                batchCommand.Transaction = transaction;
                batchCommand.ExecuteNonQuery();
            }
            return base.SaveChanges();
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var batchCommand = PrepareBatchCommand();
            if (batchCommand != null)
            {
                // external transaction varsa o set ediliyor. Yoksa biz açıyoruz ve base.savechanges'de otomatik olarak kullanılıyor.
                DbTransaction? transaction = Database.CurrentTransaction?.GetDbTransaction();

                if (transaction == null)
                {
                    transaction = (await Database.BeginTransactionAsync(cancellationToken)).GetDbTransaction();
                }

                batchCommand.Transaction = transaction;
                await batchCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    internal class BatchManager
    {
        public BatchManager()
        {
            ImmediateCommandInterceptor = new ImmediateCommandInterceptor(this);
            SaveInterceptor = new SaveInterceptor(this);
        }
        public ImmediateCommandInterceptor ImmediateCommandInterceptor { get; }
        public SaveInterceptor SaveInterceptor { get; }

        private List<DbCommand> BatchedCommands = new List<DbCommand>();
        public bool IsBatching { get; private set; }

        private DbCommand CloneCommand(DbCommand original)
        {
            var clone = original.Connection!.CreateCommand();
            clone.CommandText = original.CommandText;
            clone.CommandType = original.CommandType;

            foreach (DbParameter p in original.Parameters)
            {
                var paramClone = clone.CreateParameter();
                paramClone.ParameterName = p.ParameterName;
                paramClone.Value = p.Value;
                paramClone.DbType = p.DbType;
                paramClone.Direction = p.Direction;
                clone.Parameters.Add(paramClone);
            }
            return clone;
        }

        public void AddCommand(DbCommand command)
        {
            BatchedCommands.Add(CloneCommand(command));
            StopBatching();
        }

        public List<DbCommand> GetCommands() => BatchedCommands;

        public void StartBatching() => IsBatching = true;

        public void StopBatching() => IsBatching = false;

        public void ResetBatching()
        {
            BatchedCommands.Clear();
            StopBatching();
        }
    }

    internal class ImmediateCommandInterceptor : DbCommandInterceptor
    {

        private readonly BatchManager _batchManager;
        public ImmediateCommandInterceptor(BatchManager batchCoordinator)
        {
            _batchManager = batchCoordinator;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (!_batchManager.IsBatching) return base.NonQueryExecuting(command, eventData, result);

            if (eventData.CommandSource is CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete)
            {
                _batchManager.AddCommand(command);
                return InterceptionResult<int>.SuppressWithResult(0);
            }
            return base.NonQueryExecuting(command, eventData, result);
        }

    }

    internal class SaveInterceptor : SaveChangesInterceptor
    {
        private readonly BatchManager _batchManager;

        public SaveInterceptor(BatchManager batchCoordinator)
        {
            _batchManager = batchCoordinator;
        }

        #region Reset after SaveChanges call in every term

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            _batchManager.ResetBatching();
            return base.SavedChanges(eventData, result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            _batchManager.ResetBatching();
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
        public override void SaveChangesFailed(DbContextErrorEventData eventData)
        {
            _batchManager.ResetBatching();
            base.SaveChangesFailed(eventData);
        }
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            _batchManager.ResetBatching();
            return base.SaveChangesFailedAsync(eventData, cancellationToken);
        }
        public override void SaveChangesCanceled(DbContextEventData eventData)
        {
            _batchManager.ResetBatching();
            base.SaveChangesCanceled(eventData);
        }
        public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
        {
            _batchManager.ResetBatching();
            return base.SaveChangesCanceledAsync(eventData, cancellationToken);
        }

        #endregion

    }

    internal static class DbCommandBatchCombiner
    {
        public static DbCommand MergeCommands(this DbConnection connection, IEnumerable<DbCommand> commands)
        {
            var combinedCommand = connection.CreateCommand();
            var sb = new StringBuilder();
            var newParams = new List<DbParameter>();
            var commandPrefix = "";

            int commandCounter = 0;
            DbParameter newParam;
            string commandText;
            int paramLocalIndex = 0;

            foreach (var cmd in commands)
            {
                commandPrefix = $"cmd{commandCounter}_";
                commandText = cmd.CommandText;
                newParams = new List<DbParameter>();

                foreach (DbParameter param in cmd.Parameters)
                {
                    string originalParamName = param.ParameterName;
                    if (!originalParamName.StartsWith("@"))
                        originalParamName = "@" + originalParamName;

                    string newParamName = $"@{commandPrefix}p{paramLocalIndex++}";

                    // Tüm command text içinde eski parametre adını yeniyle değiştir
                    commandText = Regex.Replace(commandText, $@"(?<!\w){Regex.Escape(originalParamName)}(?!\w)", newParamName);

                    newParam = combinedCommand.CreateParameter();
                    newParam.ParameterName = newParamName;
                    newParam.DbType = param.DbType;
                    newParam.Value = param.Value;
                    newParam.Direction = param.Direction;

                    newParams.Add(newParam);
                }

                sb.AppendLine(commandText + ";");
                foreach (var p in newParams)
                    combinedCommand.Parameters.Add(p);

                commandCounter++;
            }

            combinedCommand.CommandText = sb.ToString();
            return combinedCommand;
        }
    }
}