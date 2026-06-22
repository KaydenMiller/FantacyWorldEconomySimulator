using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Persistence.Configurations;

public sealed class ProductionNodeConfiguration : IEntityTypeConfiguration<ProductionNode>
{
    public void Configure(EntityTypeBuilder<ProductionNode> b)
    {
        b.ToTable("production_nodes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Facility).HasConversion<string>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.SettlementId);
        b.HasIndex(x => x.RecipeId);
        b.HasIndex(x => x.ProducerShopId);
        b.Ignore(x => x.DomainEvents);
    }
}
