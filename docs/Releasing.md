# Releasing

How a new version of the package reaches nuget.org. Publishing is driven by a git tag: nothing is stored in the repository, and with trusted publishing nothing secret is stored in GitHub either.

## Check a release locally

```bash
scripts/pack.sh 0.2.0
```

This runs the tests, packs `RebolParse.0.2.0.nupkg` and its `.snupkg` symbols into `artifacts/`, checks the package contents (the library, its XML docs, a README whose links point at GitHub), and then restores the package into a fresh project and parses something with it. Nothing is published. Run it before you tag.

## One-time setup for trusted publishing

Trusted publishing lets the workflow in `.github/workflows/build.yml` prove to nuget.org who it is, and swaps that proof for an API key that lives for a few minutes. There is no long-lived key to leak.

1. **First release by hand.** A policy belongs to a package you own, so the first version of a new package goes up with an ordinary API key: create one at nuget.org (Account, API keys, scope *Push new packages and package versions*, glob `RebolParse*`), then `NUGET_API_KEY=... scripts/pack.sh 0.1.0 --push`. (If your nuget.org account lets you create a policy before the package exists, you can skip this step; I have not checked.)
2. **Add the policy.** On nuget.org, open your account menu, *Trusted Publishing*, and create a policy: repository owner `kealist`, repository `rebol-parse`, workflow file `build.yml`. In the *environment name* field type `nuget`, which is the name of the environment the `publish` job runs in. The two must match exactly. (Leaving the field blank also works, since the policy then accepts any environment.)
3. **Tell the workflow who you are.** In the GitHub repository settings add a secret `NUGET_USER` containing your nuget.org *profile name* (not your email). It is only a name, not a key.
4. In the GitHub repository settings, *Environments*, open `nuget` (it is created the first time the job runs, or you can create it first). Adding yourself as a *required reviewer* there makes every release wait for your approval, so a tag pushed by mistake cannot publish.
5. Delete the API key from step 1.

## Releasing a version

```bash
scripts/pack.sh 0.2.0          # optional: check it
git tag v0.2.0
git push origin v0.2.0
```

The tag's number is the package version (`v0.2.0` becomes `0.2.0`). The workflow builds and tests on Linux, packs, and then the `publish` job logs in with its short-lived token and pushes. Pushes to `main` and pull requests run the same build and tests without publishing, and produce a `0.0.0-ci.N` package as a downloadable artifact.

Before tagging, move the entries under *Unreleased* in `CHANGELOG.md` to the new version.

## If something goes wrong

- **The push is rejected as a duplicate.** nuget.org never lets a version be replaced. The workflow uses `--skip-duplicate`, so a re-run does nothing; tag the next version.
- **The login step fails.** The policy must match the repository owner, repository name and workflow file name exactly, and the job needs `id-token: write` (it has it). A tag pushed from a fork or a differently named workflow will not match.
- **A bad version was published.** Unlist it on nuget.org (Manage package, the version, *Listing*), then release a fixed one.
- **The version in `RebolParse.csproj`** is only used for local builds. The workflow and `scripts/pack.sh <version>` override it, so you do not need to edit it to release.
