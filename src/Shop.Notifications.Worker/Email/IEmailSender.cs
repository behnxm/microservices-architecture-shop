using System.Threading;
using System.Threading.Tasks;

namespace Shop.Notifications.Worker.Email;

public interface IEmailSender
{
    Task SendEmailAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default);
}
