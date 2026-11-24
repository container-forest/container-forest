# Changelog

## [0.11.1](https://github.com/container-forest/container-forest/compare/v0.11.0...v0.11.1) (2026-11-24)


### Bug Fixes

* **deploy:** publish images as ghcr.io/container-forest/&lt;name&gt; ([92975cf](https://github.com/container-forest/container-forest/commit/92975cf8db1bd45e0cb5e95b203912681486498b))
* **deploy:** publish images as ghcr.io/container-forest/&lt;name&gt; ([d169542](https://github.com/container-forest/container-forest/commit/d169542260efc858400c6b31abbf21cfde49380e))

## [0.11.0](https://github.com/container-forest/container-forest/compare/v0.10.1...v0.11.0) (2026-11-24)


### Features

* require the code with an administrator's password, Discord optional ([ee0a8a5](https://github.com/container-forest/container-forest/commit/ee0a8a57764b800e9915b08623b6bac970969909))
* roll back a stack to a commit it deployed before ([e07486e](https://github.com/container-forest/container-forest/commit/e07486e25555d09df47504bfd0052ee7161ca994))
* roll back a stack to a commit it deployed before ([857ee38](https://github.com/container-forest/container-forest/commit/857ee3821a5e4ca1c9831099cb9a95593b79f44e))
* two-factor authentication with an authenticator app ([c2547da](https://github.com/container-forest/container-forest/commit/c2547dabd291dfbbd29817704444df3e10bac10c))
* two-factor authentication with an authenticator app ([4d42a73](https://github.com/container-forest/container-forest/commit/4d42a738b786485f04f30d08dfcef3b1e7b0f472))

## [0.10.1](https://github.com/lepterion/container-forest/compare/v0.10.0...v0.10.1) (2026-11-23)


### Bug Fixes

* **web:** keep byte axis labels below 1000 of a unit ([a0d3d4b](https://github.com/lepterion/container-forest/commit/a0d3d4b761b6dae34381cce668d33cb57f56cc62))
* **web:** keep byte axis labels below 1000 of a unit ([6544296](https://github.com/lepterion/container-forest/commit/6544296c8b926d4609aeb5c4cae50f29212981e2))

## [0.10.0](https://github.com/lepterion/container-forest/compare/v0.9.0...v0.10.0) (2026-11-16)


### Features

* **agent:** collect container and host resource usage ([95cc15d](https://github.com/lepterion/container-forest/commit/95cc15dbe6865c3f3d8e42642b445d375f558a28))
* **aggregator:** notify about high resource usage ([083856e](https://github.com/lepterion/container-forest/commit/083856e173313345e7f7dcf87f1f2b34a014d65d))
* **aggregator:** store resource usage and serve it to the panel ([1bdc9d2](https://github.com/lepterion/container-forest/commit/1bdc9d29d1eab2261f96e5bd6e7b9f9782a21ae1))
* **bot:** /stats shows an environment's host and busiest containers ([8b1a815](https://github.com/lepterion/container-forest/commit/8b1a8154995ef17629114e3ccb1096e97fac65ae))
* **cli:** show resource usage with forest container stats and env stats ([d6be532](https://github.com/lepterion/container-forest/commit/d6be5329c1e0204ec9abf98999a27a0e7e2ef213))
* resource usage metrics for containers, stacks and hosts ([81ce7d4](https://github.com/lepterion/container-forest/commit/81ce7d43d339931c727979c7f2f4cfe22fab337e))
* **web:** back links return to the page a detail page was opened from ([b2c8da5](https://github.com/lepterion/container-forest/commit/b2c8da53e3bbade3ed208926f1a6b93c14c61a1c))
* **web:** make English the default panel language ([2dd12bd](https://github.com/lepterion/container-forest/commit/2dd12bdd313f3f959c6046d188a2bbc56f111f26))
* **web:** make English the default panel language ([01c15bd](https://github.com/lepterion/container-forest/commit/01c15bdd1077385fe87addd94c3edea54db2422f))
* **web:** resource usage graphs for containers, stacks and environments ([957bb9c](https://github.com/lepterion/container-forest/commit/957bb9c3240b9113ddfc0b0dd21622889e57fac4))
* **web:** resource usage graphs on unmanaged stack pages ([1051571](https://github.com/lepterion/container-forest/commit/1051571d02daba790628eaa328b5ee2299bc1c59))


### Bug Fixes

* **web:** byte axes step in round binary units ([ef80bf4](https://github.com/lepterion/container-forest/commit/ef80bf4e4e716a2ada3f73dd3d5222b56b36b7b6))
* **web:** container metrics subtitle and value axes ([4feb1fe](https://github.com/lepterion/container-forest/commit/4feb1fe68eafcbb0aebce665ce75c71c24dd180c))
* **web:** override minimatch under ts-morph to 11.0.8 ([5f94526](https://github.com/lepterion/container-forest/commit/5f9452621e4c71c47b5532b98532ad41dad85043))

## [0.9.0](https://github.com/lepterion/container-forest/compare/v0.8.0...v0.9.0) (2026-11-13)


### Features

* **web:** filter the stacks list by type, state and text ([bfdfac9](https://github.com/lepterion/container-forest/commit/bfdfac92fdb607e189d8a218f837bacb96aebd43))
* **web:** give unmanaged stacks a page of their own ([5ef3abf](https://github.com/lepterion/container-forest/commit/5ef3abf7417e96bbb5aae875c7da9e80d39084d3))


### Bug Fixes

* **web:** drop the compose command from unmanaged stack action labels ([ad04f37](https://github.com/lepterion/container-forest/commit/ad04f3728ebdb7b55ddf30378784e1e56ba30a77))
* **web:** keep the URL in step when an extension patches the History API ([c93dfe5](https://github.com/lepterion/container-forest/commit/c93dfe54eb6a72e9c25d0823b2ab26d501f882d2))

## [0.8.0](https://github.com/lepterion/container-forest/compare/v0.7.0...v0.8.0) (2026-11-13)


### Features

* **agent:** find and run unmanaged compose stacks, remove containers ([82df4d8](https://github.com/lepterion/container-forest/commit/82df4d836b3ebc9840831e43a378099bef187f33))
* **aggregator:** unmanaged stack operations and container removal ([d70b0ec](https://github.com/lepterion/container-forest/commit/d70b0ecaf68a1d7bf054206206a5d9b6e07e6309))
* **bot:** /stack-up, /stack-pull and /remove; unmanaged stacks in /stacks ([273cf40](https://github.com/lepterion/container-forest/commit/273cf402a00e50f7ef6c0a299e9ae77d57a7b7af))
* **cli:** forest unmanaged and forest container remove ([330471f](https://github.com/lepterion/container-forest/commit/330471f594a1a63813865b7e46ec29dfcac0ce26))
* **contracts:** add unmanaged stacks and container removal ([e03e73c](https://github.com/lepterion/container-forest/commit/e03e73cab86939185c2cb314fbda3816cbe8ea42))
* **web:** unmanaged stacks section and container removal ([d863c8a](https://github.com/lepterion/container-forest/commit/d863c8acae45d833f89ac158994f8662118aef86))
