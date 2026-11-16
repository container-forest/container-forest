# Changelog

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
