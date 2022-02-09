1. Increment the version number referenced during build in [`Versioning.props`](tools/CustomMSBuild/Versioning.props) by using [semenatic versioning](https://semver.org/)
2. Kick off a new [nightly build](https://identitydivision.visualstudio.com/OData/_build?definitionId=1104) to generate the nuget packages that will be published for this release
3. Generate release notes in the [change log](https://github.com/MicrosoftDocs/OData-docs/blob/main/Odata-docs/changelog/odatalib-7x.md) to be published on release of the new version. This is done by referencing the PRs that have been merged into `main` since the last version increment. In github, each commit should have a link to the PR that was merged to generate that commit, so you can use github history to generate the changelog. 
4. Download the build artifacts from the nightly build to your local machine
5. Mark the nightly build to be retained indefinitely
