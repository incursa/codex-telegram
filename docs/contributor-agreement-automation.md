# Contributor Agreement Automation

This repository uses the Incursa-owned contributor agreement action:

```text
incursa/contributor-agreement-action@v0.1.0
```

The action checks pull request contributors, comments with the required signing instructions, records signatures in a private repository, and publishes the `Contributor Agreement` commit status that branch rulesets can require.

This document also serves as a safe target for contributor-agreement workflow smoke tests.

## Storage

Signatures are stored outside this repository in the private repository:

```text
incursa/contributor-agreements
```

The shared signature file is:

```text
signatures/incursa-contributor-agreement-v1.json
```

Do not create that file manually. The action creates it on the first signature.

## Required Secret

The workflow expects this organization or repository secret:

```text
INCURSA_CONTRIBUTOR_AGREEMENTS_TOKEN
```

Use a fine-grained personal access token, not a classic broad `repo` token.

Recommended token settings:

1. Resource owner: `incursa`.
2. Repository access: only `incursa/contributor-agreements`.
3. Repository permissions: Contents read/write and Metadata read.
4. Expiration: choose an operationally reasonable rotation window.

Store the token as an Actions secret, not a variable.

Organization-level setup after creating the token:

```powershell
gh auth refresh -s admin:org
gh secret set INCURSA_CONTRIBUTOR_AGREEMENTS_TOKEN --org incursa --visibility all
```

Repository-level fallback for private-repo testing:

```powershell
gh secret set INCURSA_CONTRIBUTOR_AGREEMENTS_TOKEN --repo incursa/codex-telegram
```

## Pull Request Flow

The workflow runs on `pull_request_target` and selected pull request comments. It does not check out or execute pull request code.

Comments and statuses are published through `secrets.GITHUB_TOKEN`, so GitHub shows the actor as `github-actions[bot]`. The message text is customized for Incursa, but the bot name and avatar are not customizable without moving to a GitHub App or a dedicated machine-user token.

If a contributor has not signed, the action comments with the contributor agreement link and the exact phrase to post:

```text
I have read the Incursa Contributor Agreement and I hereby assign my contribution rights as described.
```

To re-run the check manually, comment:

```text
recheck contributor agreement
```

## Required Status Check

Use this required status check name in branch rulesets:

```text
Contributor Agreement
```

Do not make it required until the secret exists and at least one test pull request has proven the workflow.

## Reuse In Other Repositories

To apply this baseline to another Incursa repository:

1. Copy `.github/workflows/contributor-agreement.yml`.
2. Copy or adapt `CONTRIBUTOR-AGREEMENT.md`.
3. Confirm the repository can read `INCURSA_CONTRIBUTOR_AGREEMENTS_TOKEN`.
4. Open a test pull request from a non-allowlisted account.
5. Sign with the exact comment phrase.
6. Confirm the signature is written to `incursa/contributor-agreements`.
7. Add `Contributor Agreement` as a required status check after the flow is proven.

The action implementation lives in `incursa/contributor-agreement-action`, so future automation changes should usually be made there instead of copying logic into every repository.
