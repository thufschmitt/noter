{
  description = "Longhand: a handwriting-first notebook for Claude (Windows app and web version)";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "aarch64-darwin" "x86_64-darwin" ];
      forAll = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      # `nix develop`: everything the Linux side of the repo needs. The Windows app itself only builds on Windows (CI does it).
      devShells = forAll (pkgs: {
        default = pkgs.mkShell {
          packages = [
            pkgs.dotnet-sdk_10
            pkgs.nodejs_22
            pkgs.yq-go # for looking at the workflow
          ] ++ pkgs.lib.optional pkgs.stdenv.hostPlatform.isLinux pkgs.chromium; # the browser check drives it

          env = {
            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";
            # In a jj workspace the git calls MSBuild makes for source link fail; nothing here needs them.
            EnableSourceControlManagerQueries = "false";
          };

          shellHook = ''
            echo "Longhand dev shell. Try:"
            echo "  dotnet run --project Longhand.Web            # the web version, on http://127.0.0.1:5080"
            echo "  nix run .#dev                                # the same, restarting and reloading the browser as you edit"
            echo "  dotnet run --project tools/CoreCheck         # shared logic checks"
            echo "  dotnet build tools/XamlCheck                 # the Windows app's code, type-checked here"
            echo "  dotnet run --project tools/WebCheck          # server checks"
            echo "  tools/BrowserCheck/run.sh chromium           # real-browser checks"
          '';
        };
      });

      # `nix run .#web` starts the web version; `nix run .#check` runs every check that works off Windows.
      apps = forAll (pkgs:
        let
          runtimeInputs = [ pkgs.dotnet-sdk_10 pkgs.nodejs_22 pkgs.bash pkgs.coreutils ]
            ++ pkgs.lib.optional pkgs.stdenv.hostPlatform.isLinux pkgs.chromium;
          env = ''
            export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 EnableSourceControlManagerQueries=false
          '';
          app = name: description: text: {
            type = "app";
            program = pkgs.lib.getExe (pkgs.writeShellApplication { inherit name runtimeInputs; text = env + text; });
            meta.description = description;
          };
        in
        {
          web = app "longhand-web" "Run the web version" ''
            root=$(git rev-parse --show-toplevel 2>/dev/null || pwd)
            exec dotnet run --project "$root/Longhand.Web" -- "$@"
          '';
          dev = app "longhand-dev" "Run the web version for development: restarts on C# changes, reloads the browser on page changes" ''
            root=$(git rev-parse --show-toplevel 2>/dev/null || pwd)
            cd "$root"
            exec dotnet watch --project Longhand.Web --no-hot-reload run -- --www "$root/Longhand.Web/wwwroot" "$@"
          '';
          check = app "longhand-check" "Run every check that works off Windows" ''
            root=$(git rev-parse --show-toplevel 2>/dev/null || pwd)
            cd "$root"
            dotnet run --project tools/CoreCheck
            dotnet build tools/XamlCheck
            dotnet run --project tools/WebCheck
            ${pkgs.lib.optionalString pkgs.stdenv.hostPlatform.isLinux "tools/BrowserCheck/run.sh chromium"}
          '';
          default = self.apps.${pkgs.stdenv.hostPlatform.system}.web;
        });

      formatter = forAll (pkgs: pkgs.nixpkgs-fmt);
    };
}
