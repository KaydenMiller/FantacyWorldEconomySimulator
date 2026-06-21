using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> b)
    {
        b.ToTable("recipes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Facility).HasConversion<string>();
        b.Property(x => x.Lines).HasConversion<RecipeLinesConverter>();
        b.Property(x => x.LaborCost);
        b.Property(x => x.TicksToProduce);
        b.HasIndex(x => x.WorldId);
        b.Ignore(x => x.DomainEvents);
        b.Ignore(x => x.Inputs);
        b.Ignore(x => x.Outputs);
    }
}
