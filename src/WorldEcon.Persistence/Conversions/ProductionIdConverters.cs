using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Persistence.Conversions;

public sealed class ResourceEndowmentIdConverter() : ValueConverter<ResourceEndowmentId, Guid>(v => v.Value, g => new ResourceEndowmentId(g));
public sealed class ProductionNodeIdConverter() : ValueConverter<ProductionNodeId, Guid>(v => v.Value, g => new ProductionNodeId(g));
public sealed class RecipeIdConverter() : ValueConverter<RecipeId, Guid>(v => v.Value, g => new RecipeId(g));
public sealed class WorkOrderIdConverter() : ValueConverter<WorkOrderId, Guid>(v => v.Value, g => new WorkOrderId(g));
