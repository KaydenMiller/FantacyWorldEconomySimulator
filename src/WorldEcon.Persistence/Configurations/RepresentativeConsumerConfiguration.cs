using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class RepresentativeConsumerConfiguration : IEntityTypeConfiguration<RepresentativeConsumer>
{
    public void Configure(EntityTypeBuilder<RepresentativeConsumer> b)
    {
        b.ToTable("consumers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Budget).HasConversion<MoneyConverter>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.Seat);
        b.Ignore(x => x.DomainEvents);
    }
}
