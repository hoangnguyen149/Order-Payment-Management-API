namespace OrderPayment.Domain;

public sealed class DomainRuleException(string message) : Exception(message);
