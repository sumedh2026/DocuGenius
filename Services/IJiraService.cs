using DocuGenious.Models;

namespace DocuGenious.Services;

public interface IJiraService
{
    Task<JiraTicket> GetTicketAsync(string ticketId);
    Task<List<JiraTicket>> GetTicketsAsync(IEnumerable<string> ticketIds);
    Task<bool> ValidateConnectionAsync();
}
