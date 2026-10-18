#!/usr/bin/env bash
# Trusted publishing, step by step. Run by .github/workflows/build.yml in the publish job.
#
#   1. ask GitHub for an identity token whose audience is nuget.org
#   2. send it to nuget.org's token endpoint with your profile name
#   3. nuget.org checks the token against your trusted publishing policy (repository, workflow file, environment)
#      and answers with an API key that expires after a few minutes
#
# The key is masked in the log and written to the file named by GITHUB_OUTPUT as `api_key`.
# Needs: NUGET_USER (your nuget.org profile name) and, from the job's id-token permission,
# ACTIONS_ID_TOKEN_REQUEST_URL and ACTIONS_ID_TOKEN_REQUEST_TOKEN.
set -euo pipefail

: "${NUGET_USER:?NUGET_USER is empty: add your nuget.org profile name as a repository secret called NUGET_USER}"
: "${ACTIONS_ID_TOKEN_REQUEST_URL:?no identity token available: the job needs 'permissions: id-token: write'}"
: "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:?no identity token available: the job needs 'permissions: id-token: write'}"
audience="${NUGET_AUDIENCE:-https://www.nuget.org}"
endpoint="${NUGET_TOKEN_ENDPOINT:-https://www.nuget.org/api/v2/token}"

oidc=$(curl -sS --fail-with-body -H "Authorization: bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" \
  "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=$audience" | jq -er .value)
echo "::add-mask::$oidc"

response=$(curl -sS -w '\n%{http_code}' -X POST "$endpoint" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $oidc" \
  -d "$(jq -nc --arg user "$NUGET_USER" '{username: $user, tokenType: "ApiKey"}')")
status=${response##*$'\n'}
body=${response%$'\n'*}
if [ "$status" != "200" ]; then
  echo "nuget.org refused the token exchange (HTTP $status). Check that the trusted publishing policy names this repository, the workflow file build.yml and the environment 'nuget', and that NUGET_USER is your profile name." >&2
  echo "$body" | jq -r '.message // .error // .' >&2 2>/dev/null || echo "$body" >&2
  exit 1
fi

key=$(echo "$body" | jq -er .apiKey)
echo "::add-mask::$key"
echo "api_key=$key" >> "${GITHUB_OUTPUT:?GITHUB_OUTPUT is not set}"
echo "got a short-lived nuget.org API key"
