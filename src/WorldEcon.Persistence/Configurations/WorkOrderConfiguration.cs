using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public void Configure(EntityTypeBuilder<WorkOrder> b)
    {
        b.ToTable("work_orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.StartTick).HasConversion<TickConverter>();
        b.Property(x => x.CompleteTick).HasConversion<TickConverter>();
        b.Property(x => x.CommittedInputCost).HasConversion<MoneyConverter>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.ProductionNodeId);
        b.HasIndex(x => x.CompleteTick); // due-batch queries
        b.Ignore(x => x.DomainEvents);
    }
}
