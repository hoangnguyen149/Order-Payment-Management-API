using FluentAssertions;
using OrderPayment.Domain;
using Xunit;

namespace OrderPayment.UnitTests;

public sealed class PaymentDomainTests
{
    [Theory]
    [InlineData(PaymentStatus.Succeeded)]
    [InlineData(PaymentStatus.Failed)]
    public void Only_first_terminal_result_changes_payment(PaymentStatus first)
    {
        var payment = new Payment(Guid.NewGuid(), "create-1", 10.25m);
        payment.Apply(first).Should().BeTrue();
        var firstTimestamp = payment.UpdatedAt;
        payment.Apply(first).Should().BeFalse();
        payment.Apply(first == PaymentStatus.Succeeded ? PaymentStatus.Failed : PaymentStatus.Succeeded).Should().BeFalse();
        payment.Status.Should().Be(first);
        payment.UpdatedAt.Should().Be(firstTimestamp);
    }

    [Fact]
    public void Pending_is_not_a_terminal_callback_result()
    {
        var payment = new Payment(Guid.NewGuid(), "create-1", 10.25m);
        Action apply = () => payment.Apply(PaymentStatus.Pending);
        apply.Should().Throw<DomainRuleException>();
        payment.Status.Should().Be(PaymentStatus.Pending);
    }

    [Fact]
    public void Each_payment_has_stable_unique_reference()
    {
        var order = Guid.NewGuid();
        var first = new Payment(order, "key-1", 10m);
        var second = new Payment(order, "key-2", 10m);
        first.PaymentReference.Should().NotBe(second.PaymentReference);
        var reference = first.PaymentReference;
        first.Apply(PaymentStatus.Succeeded);
        first.PaymentReference.Should().Be(reference);
    }

    [Fact]
    public void Outbox_retries_preserve_payload_and_success_prevents_reschedule()
    {
        var message = new OutboxMessage("PaymentSucceeded", "{\"paymentId\":\"demo\"}");
        var next = DateTimeOffset.UtcNow.AddSeconds(30);
        message.MarkFailed("TransientFailure", next);
        message.Attempts.Should().Be(1);
        message.NextAttemptAt.Should().Be(next);
        message.Payload.Should().Be("{\"paymentId\":\"demo\"}");
        message.MarkProcessed();
        var processed = message.ProcessedAt;
        message.MarkProcessed();
        message.ProcessedAt.Should().Be(processed);
        message.LastError.Should().BeNull();
        message.NextAttemptAt.Should().BeNull();
        Action retry = () => message.MarkFailed("Again", next);
        retry.Should().Throw<DomainRuleException>();
    }

    [Theory]
    [InlineData(" external-id")]
    [InlineData("external-id ")]
    public void Marketplace_opaque_identifier_rejects_surrounding_whitespace(string externalId)
    {
        var order = new Order(Guid.NewGuid(), [new OrderItem(Guid.NewGuid(), "A", 10m, 1)]);
        Action assign = () => order.AssignMarketplaceId(externalId, DateTimeOffset.UtcNow);
        assign.Should().Throw<DomainRuleException>();
    }

    [Fact]
    public void Checkpoint_cannot_move_backwards_after_restart()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var checkpoint = new SyncCheckpoint("market", timestamp);
        Action rewind = () => checkpoint.AdvanceTo(timestamp.AddSeconds(-1));
        rewind.Should().Throw<DomainRuleException>();
        checkpoint.LastSyncedAtUtc.Should().Be(timestamp);
    }

    [Fact]
    public void New_failure_payload_reopens_resolved_item_for_replay()
    {
        var failure = new SyncFailure("external-1", "{\"revision\":1}", "OldFailure");
        failure.Resolve();
        failure.ReplacePayload("{\"revision\":2}", "NewFailure");
        failure.ResolvedAt.Should().BeNull();
        failure.Attempts.Should().Be(0);
        failure.Payload.Should().Contain("2");
        failure.Error.Should().Be("NewFailure");
    }
}
