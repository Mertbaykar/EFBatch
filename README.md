# EFBatch - Entity Framework Core Batch Operations

**EFBatch** is an extension for Entity Framework Core that enables efficient batching of SQL commands such as bulk update, delete, and insert-from-query. It integrates seamlessly with EF Core's `DbContext` and transaction management, allowing you to execute multiple DML operations in a single database roundtrip.

## Features

- **Batch Update**: Update multiple records with a single SQL command.
- **Batch Delete**: Delete multiple records efficiently.
- **Batch Insert From Query**: Insert records into a table based on a LINQ query.
- **Transaction Support**: All batch operations participate in the current transaction or create a new one if needed.
- **EF Core Integration**: Works with EF Core's change tracking and `SaveChanges`/`SaveChangesAsync` flow.

## Installation

## Getting Started

### 1. Inherit from `BatchingDbContext`

using EFBatch; using Microsoft.EntityFrameworkCore;
public class HRContext : BatchingDbContext<HRContext> { public HRContext(DbContextOptions<HRContext> options) : base(options) { }
public DbSet<Position> Position { get; set; }
public DbSet<RoleGroup> RoleGroup { get; set; }
public DbSet<PositionRoleGroup> PositionRoleGroup { get; set; }

// ... OnModelCreating, etc.
}

### 2. Register Your Context
builder.Services.AddDbContext<HRContext>(options => options.UseSqlServer(builder.Configuration.GetConnectionString("sqlConnection")));

### 3. Usage Examples

#### Batch Delete

Delete all `PositionRoleGroup` records for a given position:
_hrContext.BatchDelete(context => context.PositionRoleGroup.Where(x => x.PositionId == positionUpdateRequest.PositionId)); await _hrContext.SaveChangesAsync();

#### Batch Update

Update the name of a position:
_hrContext.BatchUpdate( context => context.Position.Where(x => x.Id == positionUpdateRequest.PositionId), x => x.SetProperty(y => y.Name, positionUpdateRequest.Name));
await _hrContext.SaveChangesAsync();

#### Batch Insert From Query

Insert into `PositionRoleGroup` for all positions that do not already have a specific role group:
_hrContext.BatchInsertFromQuery(context => context.Position .Where(x => !x.RoleGroups.Any(y => y.Id == rolegroupid)) .Select(x => new PositionRoleGroup(x.Id, rolegroupid)));
await _hrContext.SaveChangesAsync();

## How It Works

- **Batching**: When you call a batch method (`BatchUpdate`, `BatchDelete`, `BatchInsertFromQuery`), the SQL command is intercepted and queued.
- **Execution**: On `SaveChanges` or `SaveChangesAsync`, all batched commands are executed in a single transaction, followed by EF Core's normal change tracking operations.
- **Transactions**: If a transaction is already open, batch commands participate in it. Otherwise, a new transaction is created.

## API Reference

## Notes

- All batch operations are executed before EF Core's tracked changes are saved.
- You must call `SaveChanges` or `SaveChangesAsync` to execute batched commands.
- Batched commands are cleared after each save operation.


## Notes

- All batch operations are executed before EF Core's tracked changes are saved.
- You must call `SaveChanges` or `SaveChangesAsync` to execute batched commands.
- Batched commands are cleared after each save operation.

