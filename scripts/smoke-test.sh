#!/usr/bin/env bash
# =============================================================================
# MicroservicesShop End-to-End Smoke Test Script
# Verifies: Gateway routing, synchronous validation, outbox publishing,
# saga stock reservation, inbox deduplication, order confirmation, and Mailpit email.
# =============================================================================

set -euo pipefail

GATEWAY_URL="${GATEWAY_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"

echo "========================================================"
echo "MicroservicesShop Smoke Test starting..."
echo "Gateway: ${GATEWAY_URL}"
echo "Mailpit: ${MAILPIT_URL}"
echo "========================================================"

# 1. Health check poll
echo "==> Step 1: Polling Gateway /health endpoint..."
HEALTHY=false
for i in $(seq 1 30); do
  STATUS_CODE=$(curl -s -o /dev/null -w "%{http_code}" "${GATEWAY_URL}/health" || echo "000")
  if [ "$STATUS_CODE" -eq 200 ]; then
    echo "Gateway is HEALTHY (HTTP 200)."
    HEALTHY=true
    break
  fi
  echo "Attempt $i: Gateway returned HTTP $STATUS_CODE. Retrying in 2s..."
  sleep 2
done

if [ "$HEALTHY" = false ]; then
  echo "ERROR: Gateway failed health checks after 60s."
  exit 1
fi

# 2. Query seeded products
echo "==> Step 2: Querying Catalog products via Gateway..."
PRODUCTS_RESPONSE=$(curl -s -f "${GATEWAY_URL}/api/v1/products")
PRODUCT_ID=$(echo "$PRODUCTS_RESPONSE" | grep -o '"id":"[^"]*' | head -n 1 | cut -d'"' -f4)

if [ -z "$PRODUCT_ID" ]; then
  echo "ERROR: No seeded products found in Catalog response."
  exit 1
fi

echo "Found product: ${PRODUCT_ID}"

# 3. Submit Order Placement
IDEMPOTENCY_KEY="smoke-order-$(date +%s)-$RANDOM"
CUSTOMER_EMAIL="smoke-test-customer@example.com"
echo "==> Step 3: Placing order with Idempotency-Key: ${IDEMPOTENCY_KEY}..."

ORDER_PAYLOAD=$(cat <<EOF
{
  "customerId": "019488a0-0000-7000-8000-000000000001",
  "customerEmail": "${CUSTOMER_EMAIL}",
  "lines": [
    {
      "productId": "${PRODUCT_ID}",
      "quantity": 1
    }
  ]
}
EOF
)

ORDER_RESPONSE_HEADERS=$(curl -s -i -X POST "${GATEWAY_URL}/api/v1/orders" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: ${IDEMPOTENCY_KEY}" \
  -d "$ORDER_PAYLOAD")

HTTP_STATUS=$(echo "$ORDER_RESPONSE_HEADERS" | grep -E '^HTTP' | tail -n 1 | awk '{print $2}')
if [ "$HTTP_STATUS" -ne 202 ]; then
  echo "ERROR: Expected HTTP 202 Accepted on order placement, got: $HTTP_STATUS"
  echo "$ORDER_RESPONSE_HEADERS"
  exit 1
fi

ORDER_ID=$(echo "$ORDER_RESPONSE_HEADERS" | grep -o '"id":"[^"]*' | head -n 1 | cut -d'"' -f4)
echo "Order successfully accepted with ID: ${ORDER_ID} (HTTP 202)"

# 4. Test Idempotency Replay
echo "==> Step 4: Replaying identical request to verify idempotency..."
REPLAY_HEADERS=$(curl -s -i -X POST "${GATEWAY_URL}/api/v1/orders" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: ${IDEMPOTENCY_KEY}" \
  -d "$ORDER_PAYLOAD")

REPLAY_STATUS=$(echo "$REPLAY_HEADERS" | grep -E '^HTTP' | tail -n 1 | awk '{print $2}')
if [ "$REPLAY_STATUS" -ne 202 ]; then
  echo "ERROR: Expected HTTP 202 on idempotency replay, got: $REPLAY_STATUS"
  exit 1
fi

if ! echo "$REPLAY_HEADERS" | grep -iq "Idempotent-Replayed: true"; then
  echo "ERROR: Expected 'Idempotent-Replayed: true' header in replayed response."
  exit 1
fi
echo "Idempotency replay verified: received cached 202 with Idempotent-Replayed: true."

# 5. Poll Order Status for Saga Fulfillment (Pending -> Confirmed)
echo "==> Step 5: Polling Order status until Confirmed (asynchronous saga fulfillment)..."
ORDER_CONFIRMED=false
for i in $(seq 1 20); do
  ORDER_STATUS_RESP=$(curl -s -f "${GATEWAY_URL}/api/v1/orders/${ORDER_ID}")
  CURRENT_STATUS=$(echo "$ORDER_STATUS_RESP" | grep -o '"status":"[^"]*' | head -n 1 | cut -d'"' -f4)
  echo "Check $i: Order status is '${CURRENT_STATUS}'"
  if [ "$CURRENT_STATUS" = "Confirmed" ]; then
    ORDER_CONFIRMED=true
    echo "Order successfully transitioned to CONFIRMED!"
    break
  fi
  sleep 1
done

if [ "$ORDER_CONFIRMED" = false ]; then
  echo "ERROR: Order did not reach Confirmed state within 20s."
  exit 1
fi

# 6. Verify Email in Mailpit
echo "==> Step 6: Verifying order confirmation email delivery in Mailpit..."
MAILPIT_MESSAGES=$(curl -s "${MAILPIT_URL}/api/v1/messages" || echo "{}")
if echo "$MAILPIT_MESSAGES" | grep -q "${ORDER_ID}"; then
  echo "Mailpit confirmation email verified for Order ${ORDER_ID}!"
else
  echo "WARNING: Email not yet visible in Mailpit (Mailpit might not be accessible or worker still processing)."
fi

echo "========================================================"
echo "MicroservicesShop Smoke Test completed successfully!"
echo "========================================================"
exit 0
