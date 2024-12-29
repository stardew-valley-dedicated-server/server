# Contributing
> [!WARNING]
> This section needs work.

### Commits
We use conventional commits, refer to [Conventional Commit Cheat Sheet](https://gist.github.com/qoomon/5dfcdf8eec66a051ecd85625518cfd13).

Tools and CI pipelines enforce conventional commits are not yet in place.

### Development
#### Requirements
* Docker `>=20`
* git `>=2`
* make `>=4` ([Download](https://gnuwin32.sourceforge.net/packages/make.htm))
* .NET SDK `6` ([Download](https://dotnet.microsoft.com/en-us/download/dotnet/6.0))
* Local Stardew Valley installation (use Steam)

#### Setup
Clone the repository:
```sh
mkdir sdvd-server
cd sdvd-server
git clone --recurse-submodules git@github.com:stardew-valley-dedicated-server/server.git .
```

Update `JunimoServer.csproj` to enable IDE autocomplete and build support:
```xml
<GamePath>C:\path\to\Stardew Valley</GamePath>
```

#### Usage
Build and start the server:
```sh
make dev
```

See the logs:
```sh
docker compose logs -f
```

Stop the server:
```sh
docker compose down
```

#### Decompile Stardew Valley
To decompile Stardew Valley run this script:
```sh
bash ./tools/decompile.sh
```



### Release Management
> [!WARNING]
> This section needs work.

```sh
make push
```

