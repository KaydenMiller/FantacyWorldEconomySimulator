using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Persistence.Configurations;

public sealed class ResourceEndowmentConfiguration : IEntityTypeConfiguration<ResourceEndowment>
{
    public void Configure(EntityTypeBuilder<ResourceEndowment> b)
    {
        b.ToTable("resource_endowments");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.SettlementId);
        b.HasIndex(x => x.ProducerShopId);
        b.Ignore(x => x.DomainEvents);
    }
}
