using FluentAssertions;
using FluentValidation;
using Moq;
using OrderPayment.Application;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;
using OrderPayment.Application.Services;
using OrderPayment.Application.Validation;
using OrderPayment.Domain;
using Xunit;

namespace OrderPayment.UnitTests;

public sealed class ApplicationTests
{
    [Fact]
    public async Task Invalid_product_never_reaches_persistence()
    {
        var repository = new Mock<IProductRepository>(MockBehavior.Strict);
        var service = ProductService(repository);

        Func<Task> create = () => service.CreateAsync(new ProductRequest("", "bad sku", -10m, -1), CancellationToken.None);

        await create.Should().ThrowAsync<ValidationException>();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Product_creation_normalizes_sku_maps_response_and_forwards_cancellation()
    {
        var repository = new Mock<IProductRepository>(MockBehavior.Strict);
        using var source = new CancellationTokenSource();
        repository.Setup(x => x.AddAsync(It.Is<Product>(p => p.Name == "Keyboard" && p.Sku == "KB-1" && p.Price == 19.99m), source.Token))
            .Returns(Task.CompletedTask);
        var service = ProductService(repository);

        var result = await service.CreateAsync(new ProductRequest(" Keyboard ", "kb-1", 19.99m, 5), source.Token);

        result.Id.Should().NotBeEmpty();
        result.Sku.Should().Be("KB-1");
        result.StockQuantity.Should().Be(5);
        repository.VerifyAll();
    }

    [Fact]
    public async Task Missing_product_is_application_not_found()
    {
        var repository = new Mock<IProductRepository>(MockBehavior.Strict);
        var id = Guid.NewGuid();
        repository.Setup(x => x.GetAsync(id, CancellationToken.None)).ReturnsAsync((Product?)null);
        Func<Task> get = () => ProductService(repository).GetAsync(id, CancellationToken.None);

        await get.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData(0, 20, null)]
    [InlineData(1, 101, null)]
    [InlineData(1, 20, "price; DROP TABLE Products")]
    public async Task Invalid_paging_or_sort_is_rejected_before_querying_database(int page, int size, string? sort)
    {
        var repository = new Mock<IProductRepository>(MockBehavior.Strict);
        Func<Task> list = () => ProductService(repository).ListAsync(new PageRequest { Page = page, PageSize = size, Sort = sort }, CancellationToken.None);

        await list.Should().ThrowAsync<ValidationException>();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Invalid_order_does_not_reserve_inventory()
    {
        var repository = new Mock<IOrderRepository>(MockBehavior.Strict);
        var service = OrderService(repository);
        var productId = Guid.NewGuid();
        var request = new OrderRequest(Guid.NewGuid(), [new(productId, 1), new(productId, 1)]);

        Func<Task> create = () => service.CreateAsync(request, CancellationToken.None);

        await create.Should().ThrowAsync<ValidationException>();
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10001)]
    public void Validator_rejects_order_quantities_outside_limits(int quantity)
    {
        var result = new OrderRequestValidator().Validate(new OrderRequest(Guid.NewGuid(), [new(Guid.NewGuid(), quantity)]));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(x => x.PropertyName == "Items[0].Quantity");
    }

    [Fact]
    public void Validator_rejects_null_collection_and_null_line_without_crashing()
    {
        var validator = new OrderRequestValidator();
        validator.Validate(new OrderRequest(Guid.NewGuid(), null!)).IsValid.Should().BeFalse();
        validator.Validate(new OrderRequest(Guid.NewGuid(), [null!])).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Valid_order_response_comes_from_persisted_server_priced_order()
    {
        var repository = new Mock<IOrderRepository>(MockBehavior.Strict);
        var request = new OrderRequest(Guid.NewGuid(), [new(Guid.NewGuid(), 3)]);
        var saved = new Order(request.CustomerId, [new OrderItem(request.Items[0].ProductId, "Server product", 15.50m, 3)]);
        using var source = new CancellationTokenSource();
        repository.Setup(x => x.CreateAsync(request, source.Token)).ReturnsAsync(saved);

        var response = await OrderService(repository).CreateAsync(request, source.Token);

        response.Id.Should().Be(saved.Id);
        response.TotalAmount.Should().Be(46.50m);
        response.Items.Should().ContainSingle(x => x.ProductName == "Server product" && x.UnitPrice == 15.50m);
        repository.VerifyAll();
    }

    private static ProductService ProductService(Mock<IProductRepository> repository) =>
        new(repository.Object, new ProductRequestValidator(), new PageRequestValidator());

    private static OrderService OrderService(Mock<IOrderRepository> repository) =>
        new(repository.Object, new OrderRequestValidator(), new OrderQueryValidator());
}
