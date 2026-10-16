using AmcTracker.Application.Dtos;

namespace AmcTracker.Application.Services;

/// <summary>Customer Master: create, search, view, edit and soft delete.</summary>
public interface ICustomerService
{
    /// <summary>
    /// Matches name, phone and project name (case-insensitive). <paramref name="active"/> null returns all.
    /// Page size defaults to 20 and is capped at 100.
    /// </summary>
    Task<PagedResult<CustomerDto>> SearchAsync(
        string? search, bool? active, int? page, int? pageSize, CancellationToken ct = default);

    Task<CustomerDto> GetByIdAsync(int id, CancellationToken ct = default);
    Task<CustomerDto> CreateAsync(CreateCustomerRequest request, CancellationToken ct = default);
    Task<CustomerDto> UpdateAsync(int id, UpdateCustomerRequest request, CancellationToken ct = default);

    /// <summary>Sets IsActive = false and keeps the row. An unknown or already inactive customer is "not found".</summary>
    Task SoftDeleteAsync(int id, CancellationToken ct = default);
}
