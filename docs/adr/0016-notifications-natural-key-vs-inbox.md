# ADR 0016: Notifications Natural Key Deduplication vs Inbox

## Status
Accepted

## Context
When handling events in microservices, the Transactional Inbox pattern typically deduplicates incoming messages based on `(message_id, handler_name)`.
However, outbound notifications (such as dispatching emails to customers) represent irreversible external side effects: once an SMTP server accepts and delivers an email, it cannot be undone or rolled back.

If an upstream service triggers a saga retry, an event replay, or a manual administrative re-publish, the resulting integration event will often have a fresh `MessageId`. An inbox relying solely on `message_id` would see an unfamiliar ID and trigger a duplicate email to the customer, eroding user trust.

## Decision
I implemented **Natural Key Deduplication** for customer notifications in `Shop.Notifications.Worker`:
1. **Natural Business Key**:
   - The `notifications` table defines a composite primary key consisting of `(order_id, type)` (e.g. `(Order-123, "OrderConfirmed")`).
2. **Execution Flow**:
   - Before invoking the SMTP client, the notification handler checks if a record for `(order_id, type)` already exists in `notificationsdb`.
   - If present, the message is skipped as a duplicate and acknowledged immediately.
   - If absent, the email is dispatched via SMTP (`MailKit`), and the `(order_id, type, recipient, status, sent_at)` record is persisted.
3. **Defense-in-Depth**:
   - The worker also retains the `inbox_messages` table to deduplicate identical transport re-deliveries from RabbitMQ at the consumer layer.
   - Natural key deduplication acts as the domain guardrail, preventing duplicate customer communications even across distinct message IDs and saga replays.

## Consequences
### Positive
- Guarantee that a customer receives exactly one confirmation or cancellation email per order, even if events are replayed, regenerated, or retried.
- Explicit audit trail of sent communications stored in `notificationsdb`.

### Negative / Trade-offs
- If a future requirement allows re-sending emails (e.g. "Resend order confirmation"), the key schema would need revision (such as introducing a sequence number or `attempt_id`).
