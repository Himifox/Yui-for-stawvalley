namespace YuiToIssho;

internal sealed class OperationReceiptRecord
{
    public string OperationId { get; set; } = string.Empty;

    public bool IsSuccess { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
