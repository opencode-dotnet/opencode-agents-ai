# Security Policy

## Supported Versions

This project is pre-1.0 and has not published a stable release yet. Until the first stable release ships, the
supported version is whatever is current on `master`.
Security patches are prioritized by CVSS v3.0 rating:

### Security Patch Policy

| CVSS v3.0 | 0.x (Current) |
| --------- | ------------- |
| 9.0-10.0  | ✅ Fixed and released as soon as a fix exists |
| 4.0-8.9   | ✅ Most recent release |
| < 4.0     | ⚠️ Best effort |

Once a stable line exists, this table is updated to name it explicitly.

## Reporting a Vulnerability

We take security bugs seriously. We appreciate your efforts to disclose findings responsibly, and
will make every effort to acknowledge your contribution.

**Security Infrastructure**: this repository has GitHub secret scanning and push protection
enabled, so credentials committed by mistake are caught at push time.

### Preferred Method: GitHub Security Advisories

1. Go to the [Security tab](https://github.com/opencode-dotnet/opencode-agents-ai/security) of this
   repository
2. Click **"Report a vulnerability"**
3. Fill out the security advisory form with details about the vulnerability

This keeps the report private between you and the maintainer until a fix is available.

### Public Issues

For **non-security** bugs, please use the
[GitHub Issues](https://github.com/opencode-dotnet/opencode-agents-ai/issues) tracker. Never file a
suspected vulnerability as a public issue.

## Scope

This policy covers the provider package in this repository — `OpenCodeDotNet.Agents.AI` — and the
repository's own tooling and workflows.

Explicitly **out of scope**:

- **The opencode server itself.** This is an unofficial provider; vulnerabilities in the opencode
  server, CLI, or protocol belong to [that project](https://github.com/anomalyco/opencode). If you
  are unsure which side a finding lands on, report it here and we will help route it.

## Response Timeline

We will respond to security vulnerability reports within **48 hours** and will keep you informed
throughout the process of fixing the vulnerability.

## Security Updates

Security updates are released as soon as possible after a vulnerability is confirmed and a fix is
available. We will:

1. Confirm the problem and determine the affected versions
2. Audit the code for similar problems
3. Prepare fixes for all supported versions
4. Release new versions as quickly as possible

## Comments on this Policy

If you have suggestions on how this process could be improved, please submit a pull request or open
an issue to discuss.
