
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace EFBatch
{
    public abstract class BatchingDbContext<TContext> : DbContext where TContext : BatchingDbContext<TContext>
    {

        internal BatchManager BatchManager = new();
        private ILogger SaveLogger => this.GetService<IDiagnosticsLogger<DbLoggerCategory.Update>>().Logger;

        private readonly static MethodInfo SelectMethod = typeof(Queryable)
                    .GetMethods()
                    .Where(m => m.Name == nameof(Queryable.Select) && m.IsGenericMethodDefinition)
                    .Where(m => m.GetParameters().Length == 2)
                    .First(m => m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>));

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

        public async Task BatchUpdateAsync<T>(Func<TContext, IQueryable<T>> sourceQueryFunc,
            Expression<Func<SetPropertyCalls<T>, SetPropertyCalls<T>>> setPropertyCalls) where T : class
        {
            try
            {
                BatchManager.StartBatching();
                await sourceQueryFunc((TContext)this).ExecuteUpdateAsync(setPropertyCalls);
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

        public async Task BatchDeleteAsync<T>(Func<TContext, IQueryable<T>> sourceQueryFunc) where T : class
        {
            try
            {
                BatchManager.StartBatching();
                await sourceQueryFunc((TContext)this).ExecuteDeleteAsync();
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

        public void BatchInsertFromQuery<T>(Func<TContext, IQueryable<T>> sourceQueryFunc) where T : class
        {
            try
            {
                BatchManager.StartBatching();

                IEntityType? entityType = ((TContext)this).GetService<IDesignTimeModel>().Model.FindEntityType(typeof(T))
                                          ?? throw new Exception($"{typeof(T).FullName} DbSet property is not declared in {typeof(TContext).Name}.");

                IQueryable<T> query = sourceQueryFunc((TContext)this);
                // Select ifadesini bul
                if (query.Expression is not MethodCallExpression methodCallExpr
                    || methodCallExpr.Arguments[0] is not Expression sourceExpression
                    || methodCallExpr.Method.Name != "Select")
                    throw new InvalidOperationException($"Query must end with a .Select([param] => new {typeof(T).Name}) call");

                // .Select(x => new PositionRoleGroup(...)) içindeki lambda expression
                if (methodCallExpr.Arguments[1] is not UnaryExpression unaryExpr ||
                    unaryExpr.Operand is not LambdaExpression lambdaExpr ||
                    lambdaExpr.Body is not NewExpression newExpr)
                {
                    throw new InvalidOperationException($"Select must be in the form of new {typeof(T).Name}(...) expression");
                }

                // Constructor argümanları ve parametre isimleri
                ReadOnlyCollection<Expression> ctorArgs = newExpr.Arguments;
                List<string> ctorPropArgs = newExpr.Constructor!.GetParameters().Select(x => x.Name!).ToList();

                if (ctorPropArgs.Count == 0)
                    throw new Exception("No constructor members found in new expression");

                string tableName = entityType.GetTableName()
                                ?? throw new Exception($"Table mapping for {typeof(T).FullName} not found.");

                string? schema = entityType.GetSchema();
                StoreObjectIdentifier storeObject = StoreObjectIdentifier.Table(tableName!, schema);
                string tableInsertPrefix = string.IsNullOrEmpty(schema) ? $"[{tableName}]" : $"[{schema}].[{tableName}]";

                IEnumerable<IProperty> entityProps = entityType.GetProperties();
                List<string> columnNames = new();

                string propName;
                IProperty entityProp;
                string columnName;

                for (int i = 0; i < ctorPropArgs.Count; i++)
                {
                    propName = ctorPropArgs[i];
                    entityProp = entityProps
                                .First(prop => string.Equals(prop.PropertyInfo?.Name, propName, StringComparison.OrdinalIgnoreCase))
                                ?? throw new Exception($"Property '{propName}' not found in entity type '{typeof(T).Name}'");

                    columnName = entityProp.GetColumnName(storeObject)
                                    ?? throw new Exception($"Column name for property '{propName}' not found");

                    columnNames.Add(columnName);
                }

                Expression valueExpr;
                List<MemberAssignment> bindings = new();
                IEnumerable<PropertyInfo> entityCLRProps = entityProps.Select(prop => prop.PropertyInfo)!;
                PropertyInfo propInfo;
                MemberAssignment binding;

                for (int i = 0; i < ctorArgs.Count; i++)
                {
                    propName = ctorPropArgs[i]; // örn: "PositionId"
                    valueExpr = ctorArgs[i];   // örn: x.Id veya rolegroupid
                    propInfo = entityCLRProps.First(p => propName.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                                   ?? throw new Exception($"Property '{propName}' not found on type '{typeof(T).Name}'");
                    binding = Expression.Bind(propInfo, valueExpr);
                    bindings.Add(binding);
                }

                MemberInitExpression memberInit = Expression.MemberInit(Expression.New(typeof(T)), bindings);
                ParameterExpression parameter = lambdaExpr.Parameters[0];
                // Yeni lambda: x => new T { PositionId = x.Id, RoleGroupId = rolegroupid }
                LambdaExpression newLambda = Expression.Lambda(memberInit, parameter);
                MethodInfo genericSelect = SelectMethod.MakeGenericMethod(parameter.Type, typeof(T));

                var callExpr = Expression.Call(
                                     genericSelect,
                                     sourceExpression,
                                     Expression.Quote(newLambda)
                                );
                IQueryable<T> selectQuery = query.Provider.CreateQuery<T>(callExpr);
                DbCommand dbCommand = selectQuery.CreateDbCommand();
                dbCommand.CommandText = $"""
                    INSERT INTO {tableInsertPrefix}({string.Join(", ", columnNames.Select(column => $"[{column}]"))}) 
                    {dbCommand.CommandText}
                """;
                BatchManager.AddCommand(dbCommand, false);
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
            optionsBuilder.AddInterceptors(BatchManager.CommandInterceptor, BatchManager.ConnectionInterceptor, BatchManager.SaveInterceptor);
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

        private void LogCommand(DbCommand command, double durationms)
        {
            var sb = new StringBuilder();
            sb.Append($"Executed DbCommand ({durationms}ms) ");
            sb.Append("[");

            if (command.Parameters.Count > 0)
            {
                sb.Append("Parameters=[");
                foreach (DbParameter param in command.Parameters)
                {
                    sb.Append($"({param.ParameterName}='{param.Value}', " +
                        $"DbType = {param.DbType}), ");
                }
                sb.Remove(sb.Length - 2, 2); // Remove last comma and space
                sb.Append("], ");
            }

            sb.AppendLine($"""
                    CommandType='{command.CommandType}', CommandTimeout='{command.CommandTimeout}']
                    """);
            //sb.AppendLine("]");
            sb.Append(command.CommandText);
            SaveLogger.LogInformation(sb.ToString());
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
                try
                {
                    long startTime = Stopwatch.GetTimestamp();
                    int batchResult = batchCommand.ExecuteNonQuery();
                    int result = base.SaveChanges();
                    transaction.Commit();
                    LogCommand(batchCommand, Stopwatch.GetElapsedTime(startTime).TotalMilliseconds); // Log the batch command execution
                    return result + batchResult;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    throw;
                }
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

                try
                {
                    long startTime = Stopwatch.GetTimestamp();
                    int batchResult = await batchCommand.ExecuteNonQueryAsync(cancellationToken);
                    int result = await base.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync();
                    LogCommand(batchCommand, Stopwatch.GetElapsedTime(startTime).Milliseconds); // Log the batch command execution
                    return result + batchResult;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            }
            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    internal class BatchManager
    {
        public BatchManager()
        {
            CommandInterceptor = new CommandInterceptor(this);
            ConnectionInterceptor = new ConnectionInterceptor(this);
            SaveInterceptor = new SaveInterceptor(this);
        }
        public ConnectionInterceptor ConnectionInterceptor { get; }
        public CommandInterceptor CommandInterceptor { get; }
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

        public void AddCommand(DbCommand command, bool clone = true)
        {
            if (clone) BatchedCommands.Add(CloneCommand(command));
            else BatchedCommands.Add(command);
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

    internal class ConnectionInterceptor : DbConnectionInterceptor
    {

        private readonly BatchManager _batchManager;
        public ConnectionInterceptor(BatchManager batchManager)
        {
            _batchManager = batchManager;
        }

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            if (!_batchManager.IsBatching)
                return base.ConnectionOpening(connection, eventData, result);
            return base.ConnectionOpening(connection, eventData, InterceptionResult.Suppress());
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!_batchManager.IsBatching)
                return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
            return base.ConnectionOpeningAsync(connection, eventData, InterceptionResult.Suppress(), cancellationToken);
        }
    }

    internal class CommandInterceptor : DbCommandInterceptor
    {

        private readonly BatchManager _batchManager;
        public CommandInterceptor(BatchManager batchManager)
        {
            _batchManager = batchManager;
        }

        public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
        {
            if (_batchManager.IsBatching)
                _batchManager.AddCommand(result);
            return base.CommandInitialized(eventData, result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (_batchManager.IsBatching)
                return InterceptionResult<int>.SuppressWithResult(0);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_batchManager.IsBatching)
                return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    internal class SaveInterceptor : SaveChangesInterceptor
    {
        private readonly BatchManager _batchManager;

        public SaveInterceptor(BatchManager batchManager)
        {
            _batchManager = batchManager;
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

    internal static class DbCommandExtensions
    {
        public static DbCommand MergeCommands(this DbConnection connection, IEnumerable<DbCommand> commands)
        {
            var combinedCommand = connection.CreateCommand();
            var sb = new StringBuilder();
            var newParams = new List<DbParameter>();
            string commandPrefix = "";

            int commandCounter = 0;
            DbParameter newParam;
            string commandText;
            int paramLocalIndex = 0;
            string originalParamName = "";
            string newParamName = "";

            foreach (var cmd in commands)
            {
                commandPrefix = $"cmd{commandCounter}_";
                commandText = cmd.CommandText;
                newParams = new List<DbParameter>();

                foreach (DbParameter param in cmd.Parameters)
                {
                    originalParamName = param.ParameterName;
                    if (!originalParamName.StartsWith("@"))
                        originalParamName = "@" + originalParamName;

                    newParamName = $"@{commandPrefix}p{paramLocalIndex++}";

                    // Tüm command text içinde eski parametre adını yeniyle değiştir
                    //commandText = Regex.Replace(commandText, $@"(?<!\w){Regex.Escape(originalParamName)}(?!\w)", newParamName);
                    commandText = commandText.Replace(originalParamName, newParamName);

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