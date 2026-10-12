using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Common;
using AiNative.Contracts.Erp;
using AiNative.Domain.Erp;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Erp;

[RequireFeature(ErpFeatures.View)]
public sealed record ListErpStockDocumentsQuery(
    ErpStockDocumentKind? Kind = null,
    Guid? SalesOrderId = null,
    Guid? PurchaseOrderId = null,
    Guid? ProductId = null,
    int Page = 1,
    int PageSize = 50) : IQuery<PagedResult<ErpStockDocumentDto>>;

[RequireFeature(ErpFeatures.View)]
public sealed record GetErpStockDocumentQuery(Guid Id) : IQuery<ErpStockDocumentDto>;

internal sealed class ErpStockDocumentHandlers(IAppDbContext db) :
    IRequestHandler<ListErpStockDocumentsQuery, PagedResult<ErpStockDocumentDto>>,
    IRequestHandler<GetErpStockDocumentQuery, ErpStockDocumentDto>
{
    public async Task<PagedResult<ErpStockDocumentDto>> HandleAsync(ListErpStockDocumentsQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = ErpRecords.Paging(request.Page, request.PageSize);
        var query = db.ErpStockDocuments.AsNoTracking();
        if (request.Kind is { } kind)
        {
            query = query.Where(d => d.Kind == kind);
        }

        if (request.SalesOrderId is { } salesOrderId)
        {
            query = query.Where(d => d.SalesOrderId == salesOrderId);
        }

        if (request.PurchaseOrderId is { } purchaseOrderId)
        {
            query = query.Where(d => d.PurchaseOrderId == purchaseOrderId);
        }

        if (request.ProductId is { } productId)
        {
            query = query.Where(d => d.Lines.Any(l => l.ProductId == productId));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<ErpStockDocumentDto>(await ErpMapping.DocumentsAsync(db, items, cancellationToken).ConfigureAwait(false), total, page, pageSize);
    }

    public async Task<ErpStockDocumentDto> HandleAsync(GetErpStockDocumentQuery request, CancellationToken cancellationToken)
    {
        var document = await db.ErpStockDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.Id, cancellationToken).ConfigureAwait(false)
                       ?? throw new NotFoundException("erp_stock_document", request.Id);
        return (await ErpMapping.DocumentsAsync(db, [document], cancellationToken).ConfigureAwait(false))[0];
    }
}

/// <summary>
/// 发货、收货、退货共用的单据辅助：算出本次各产品的数量、开单、按订单单价和库存成本写单据行。
/// </summary>
internal sealed class ErpDocuments(IAppDbContext db, ITenantContext tenant, ErpRecords records)
{
    /// <summary>
    /// 新开一张出入库单（加入当前事务，随订单的状态变化一起提交）。
    /// </summary>
    public async Task<ErpStockDocument> CreateAsync(
        ErpStockDocumentKind kind,
        ErpSalesOrder? salesOrder,
        ErpPurchaseOrder? purchaseOrder,
        Guid warehouseId,
        string note,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        var document = new ErpStockDocument
        {
            Number = await records.NextNumberAsync(ErpStockDocument.Prefix(kind), cancellationToken).ConfigureAwait(false),
            Kind = kind,
            SalesOrderId = salesOrder?.Id,
            PurchaseOrderId = purchaseOrder?.Id,
            WarehouseId = warehouseId,
            Currency = salesOrder?.Currency ?? purchaseOrder!.Currency,
            Note = note.Trim(),
            CreatedBy = tenant.Actor,
            RunId = runId,
        };
        db.ErpStockDocuments.Add(document);
        return document;
    }

    /// <summary>
    /// 写一行单据。金额按订单行的累计数量算差额（分几次发完时加起来正好等于整行金额），成本 = 数量 × 单位成本。
    /// </summary>
    /// <param name="before">本次之前的累计数量（已发、已收或已退）。</param>
    public static void AddLine(ErpStockDocument document, ErpOrderLine line, decimal quantity, decimal before, decimal unitCost, ErpProduct product)
    {
        var amount = line.AmountBetween(before, before + quantity);
        document.Lines.Add(new ErpStockDocumentLine
        {
            ProductId = line.ProductId,
            Quantity = quantity,
            UnitPrice = line.UnitPrice,
            Amount = amount,
            UnitCost = unitCost,
            CostAmount = ErpCosting.Amount(quantity, unitCost),
            CostCurrency = product.Currency,
        });
        document.Amount += amount;
    }

    /// <summary>
    /// 采购入库的单位成本（产品货币）：订单货币与产品相同时就是采购单价；不同时暂不做汇率换算，用产品的参考采购价，
    /// 没有参考价时为空（按仓库当前的平均成本入库）。
    /// </summary>
    public static decimal? PurchaseCost(ErpPurchaseOrder order, ErpOrderLine line, ErpProduct product)
        => order.Currency == product.Currency ? line.UnitPrice : product.Cost;

    /// <summary>
    /// 本次各产品的数量。没给明细时为每行的全部可用数量（<paramref name="limit"/>）；给了明细时按产品合计，
    /// 数量为 0 的行忽略，超过可用数量或订单里没有的产品逐条报错。
    /// </summary>
    public static Dictionary<Guid, decimal> Quantities(
        IReadOnlyList<ErpOrderLine> lines,
        IReadOnlyList<ErpQuantityLineRequest>? requested,
        Func<ErpOrderLine, decimal> limit,
        IReadOnlyDictionary<Guid, ErpProduct> products,
        string verb)
    {
        Dictionary<Guid, decimal> quantities;
        if (requested is null || requested.Count == 0)
        {
            quantities = lines.Where(l => limit(l) > 0).ToDictionary(l => l.ProductId, limit);
        }
        else
        {
            var errors = new List<string>();
            quantities = [];
            foreach (var group in requested.GroupBy(r => r.ProductId))
            {
                var quantity = group.Sum(r => r.Quantity);
                var line = lines.FirstOrDefault(l => l.ProductId == group.Key);
                var name = products.TryGetValue(group.Key, out var product) ? $"「{product.Name}」" : "这个产品";
                if (line is null)
                {
                    errors.Add($"订单里没有{name}。");
                }
                else if (quantity > limit(line))
                {
                    errors.Add($"{name}最多还能{verb} {ErpRecords.Quantity(limit(line))} {product?.Unit}。");
                }
                else if (quantity > 0)
                {
                    quantities[group.Key] = quantity;
                }
            }

            if (errors.Count > 0)
            {
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["lines"] = [.. errors] });
            }
        }

        return quantities.Count > 0
            ? quantities
            : throw new ValidationFailedException(new Dictionary<string, string[]> { ["lines"] = [$"没有可以{verb}的数量。"] });
    }

    /// <summary>
    /// 按产品 ID 排序：并发的操作按同样的顺序锁库存行，不会互相等待成死锁。
    /// </summary>
    public static IEnumerable<(ErpOrderLine Line, decimal Quantity)> Ordered(IReadOnlyList<ErpOrderLine> lines, IReadOnlyDictionary<Guid, decimal> quantities)
        => quantities.OrderBy(q => q.Key).Select(q => (lines.First(l => l.ProductId == q.Key), q.Value));
}

public sealed class ErpDeliveryRequestValidator : AbstractValidator<ErpDeliveryRequest>
{
    public ErpDeliveryRequestValidator()
    {
        RuleFor(x => x.Lines!).QuantityLines().When(x => x.Lines is not null);
        RuleFor(x => x.Note).Text(500, "备注").OverridePropertyName("note");
    }
}

public sealed class ErpReturnRequestValidator : AbstractValidator<ErpReturnRequest>
{
    public ErpReturnRequestValidator()
    {
        RuleFor(x => x.Lines).NotEmpty().WithMessage("请填写退货的产品和数量。").QuantityLines();
        RuleFor(x => x.Note).Text(500, "退货原因").OverridePropertyName("note");
    }
}

internal static partial class ErpValidation
{
    public static IRuleBuilderOptions<T, IReadOnlyList<ErpQuantityLineRequest>> QuantityLines<T>(this IRuleBuilder<T, IReadOnlyList<ErpQuantityLineRequest>> rule)
        => rule
            .Must(l => l.Count <= MaxLines).WithMessage($"最多 {MaxLines} 行。")
            .Must(l => l.All(x => x.Quantity >= 0)).WithMessage("数量不能为负数。")
            .Must(l => l.All(x => x.Quantity < MaxQuantity)).WithMessage("数量太大。")
            .Must(l => l.All(x => HasQuantityScale(x.Quantity))).WithMessage("数量最多 4 位小数。")
            .OverridePropertyName("lines");
}
