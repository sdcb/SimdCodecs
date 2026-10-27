# Releasing

Pushing a version tag runs the build and publishes its packages to NuGet.org. For example, `v1.0.0`
publishes version `1.0.0` for every package; `v1.0.0-rc.1` publishes a prerelease. Tags must use
`vMAJOR.MINOR.PATCH`, optionally followed by a SemVer prerelease suffix. Build metadata is not accepted
because NuGet does not include it in the package version identity. The packages are published only
when every CI job (build and tests, the JPEG ISA tiers, NativeAOT) has passed.

Publishing uses [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing):

1. In NuGet.org, create a trusted publishing policy for the appropriate package owner and scopes, with
   repository owner `sdcb`, repository `SimdCodecs`, and workflow file `dotnet.yml`. Leave the
   environment empty.
2. Set the GitHub Actions repository variable `NUGET_USER` to the NuGet.org username used to publish
   the packages (not an email address).
3. After a successful tag publication, revoke the old long-lived NuGet API key and remove the
   `NUGET_API_KEY` GitHub secret if no other workflows need it.

The workflow exchanges a GitHub OIDC token for a short-lived NuGet API key; no long-lived API key needs
to be stored in GitHub.
