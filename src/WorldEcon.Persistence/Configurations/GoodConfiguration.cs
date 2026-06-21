using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class GoodConfiguration : IEntityTypeConfiguration<Good>
{
    public void Configure(EntityTypeBuilder<Good> b)
    {
        b.ToTable("goods");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.BaseUnit).IsRequired();
        b.Property(x => x.Category).HasConversion<string>();
        b.Property(x => x.Size).HasConversion<string>();
        b.Property(x => x.Provenance).HasConversion<string>();
        b.Property(x => x.BaseValue).HasConversion<MoneyConverter>();
        b.HasIndex(x => x.WorldId);
        b.Ignore(x => x.DomainEvents);
    }
}
