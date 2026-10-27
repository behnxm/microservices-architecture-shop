# ADR 0015: Authentication and Authorization Boundaries

## Status
Accepted

## Context
Per scope discipline, production security credentials, OAuth2/OIDC servers, and JWT tokens were intentionally excluded from this reference architecture to focus entirely on distributed systems mechanics.

However, in an enterprise production deployment, clear boundaries for identity verification and access control are essential.

## Decision
I established the future authentication and authorization architecture:
1. **Edge Authentication (Gateway Ingress)**:
   - The Gateway (`Shop.Gateway`) is the sole termination point for external SSL/TLS and client authentication.
   - External clients authenticate against an OpenID Connect (OIDC) identity provider (e.g. Microsoft Entra ID, Keycloak, or Auth0) to obtain a signed JWT Access Token.
   - The Gateway validates the JWT signature, issuer, expiration, and audience claims via ASP.NET Core `Microsoft.AspNetCore.Authentication.JwtBearer`.
   - The Gateway strips any spoofed internal headers, extracts the verified `sub` (User ID), tenant, and permissions, and propagates them downstream using signed internal headers (e.g. `X-User-Id`, `X-User-Roles`) or an internal mutual-TLS (mTLS) token.
2. **Downstream Service Authorization**:
   - Internal microservices (`catalog-api`, `ordering-api`) trust claims forwarded by the Gateway over the private backend network.
   - Resource-level authorization (e.g. ensuring a customer can only view their own orders) is enforced within the service domain logic using `order.CustomerId == callerUserId`.
3. **Internal Mutual TLS (mTLS)**:
   - In production (e.g. Kubernetes with a service mesh like Istio or Linkerd), inter-service HTTP and AMQP traffic is encrypted and authenticated via mTLS certificates.

## Consequences
### Positive
- Downstream microservices are relieved from validating third-party identity providers or managing OAuth token refreshes.
- Internal network isolation prevents untrusted external clients from reaching service endpoints.

### Negative / Trade-offs
- Internal microservices must trust the Gateway's network boundaries and identity forwarding headers.
