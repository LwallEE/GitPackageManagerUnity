# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).


## [0.1.0] - 2025-27-12

### Added
- List all packages in a gitHub repository.
- Identify package's version by apply tag "publish/{package.name}={package.version" to commit.
- Identify package's dependencies by add 'gitHubDependencies' in package.json (Only identify dependency in gitHub repository).
- Install packages via gitHub (you must write your url of your repo and token access key (if private repository).

## [0.1.1] - 2025-29-12
- fix auto create folder of repo config.

## [0.1.2] - 2025-29-12
- remove auto create folder of repo config.
- change path to store repo config.
- add dependency on UnityEditor.Utils.
## [0.1.3] - 2025-29-12
- Change Dependencies of .asmdef UnityEditor.Utils to Lwalle.UnityEditor.Utils.
- get all tags instead of 100.
- only get package that is setted a tag
- fix redundant space when extract fields, names.
- add extracted name must start with "com" when extracts name.