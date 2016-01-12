﻿// Copyright © 2011 - Present RealDimensions Software, LLC
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// 
// You may obtain a copy of the License at
// 
// 	http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace chocolatey.infrastructure.app.services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Management.Automation;
    using System.Management.Automation.Runspaces;
    using System.Reflection;
    using System.Security.Cryptography;
    using System.Text;
    using adapters;
    using builders;
    using commandline;
    using configuration;
    using cryptography;
    using domain;
    using filesystem;
    using infrastructure.commands;
    using logging;
    using powershell;
    using results;
    using Assembly = adapters.Assembly;
    using Console = System.Console;
    using Environment = System.Environment;

    public class PowershellService : IPowershellService
    {
        private readonly IFileSystem _fileSystem;
        private readonly string _customImports;

        public PowershellService(IFileSystem fileSystem)
            : this(fileSystem, new CustomString(string.Empty))
        {
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="PowershellService" /> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="customImports">The custom imports. This should be everything you need minus the &amp; to start and the ending semi-colon.</param>
        public PowershellService(IFileSystem fileSystem, CustomString customImports)
        {
            _fileSystem = fileSystem;
            _customImports = customImports;
        }

        private string get_script_for_action(PackageResult packageResult, CommandNameType command)
        {
            var file = "chocolateyInstall.ps1";
            switch (command)
            {
                case CommandNameType.uninstall:
                    file = "chocolateyUninstall.ps1";
                    break;

                case CommandNameType.upgrade:
                    file = "chocolateyBeforeModify.ps1";
                    break;
            }

            var packageDirectory = packageResult.InstallLocation;
            var installScript = _fileSystem.get_files(packageDirectory, file, SearchOption.AllDirectories).FirstOrDefault();
            return installScript;
        }

        public void noop_action(PackageResult packageResult, CommandNameType command)
        {
            var chocoInstall = get_script_for_action(packageResult, command);
            if (!string.IsNullOrEmpty(chocoInstall))
            {
                this.Log().Info("Would have run '{0}':".format_with(chocoInstall));
                this.Log().Warn(_fileSystem.read_file(chocoInstall).escape_curly_braces());
            }
        }

        public void install_noop(PackageResult packageResult)
        {
            noop_action(packageResult, CommandNameType.install);
        }

        public bool install(ChocolateyConfiguration configuration, PackageResult packageResult)
        {
            return run_action(configuration, packageResult, CommandNameType.install);
        }

        public void uninstall_noop(PackageResult packageResult)
        {
            noop_action(packageResult, CommandNameType.uninstall);
        }

        public bool uninstall(ChocolateyConfiguration configuration, PackageResult packageResult)
        {
            return run_action(configuration, packageResult, CommandNameType.uninstall);
        }

        public void before_modify_noop(PackageResult packageResult)
        {
            noop_action(packageResult, CommandNameType.upgrade);
        }

        public bool before_modify(ChocolateyConfiguration configuration, PackageResult packageResult)
        {
            return run_action(configuration, packageResult, CommandNameType.upgrade);
        }

        private string get_helpers_folder()
        {
            return _fileSystem.combine_paths(ApplicationParameters.InstallLocation, "helpers");
        }

        public string wrap_script_with_module(string script, ChocolateyConfiguration config)
        {
            var installerModules = _fileSystem.get_files(ApplicationParameters.InstallLocation, "chocolateyInstaller.psm1", SearchOption.AllDirectories);
            var installerModule = installerModules.FirstOrDefault();
            var scriptRunners = _fileSystem.get_files(ApplicationParameters.InstallLocation, "chocolateyScriptRunner.ps1", SearchOption.AllDirectories);
            var scriptRunner = scriptRunners.FirstOrDefault();
            // removed setting all errors to terminating. Will cause too
            // many issues in existing packages, including upgrading
            // Chocolatey from older POSH client due to log errors
            //$ErrorActionPreference = 'Stop';
            return "[System.Threading.Thread]::CurrentThread.CurrentCulture = '';[System.Threading.Thread]::CurrentThread.CurrentUICulture = ''; & import-module -name '{0}';{2} & '{1}' {3}"
                .format_with(
                    installerModule,
                    scriptRunner,
                    string.IsNullOrWhiteSpace(_customImports) ? string.Empty : "& {0}".format_with(_customImports.EndsWith(";") ? _customImports : _customImports + ";"),
                    get_script_arguments(script,config)
                );
        }

        private string get_script_arguments(string script, ChocolateyConfiguration config)
        {
            return "-packageScript '{0}' -installArguments '{1}' -packageParameters '{2}'{3}{4}".format_with(
                script,
                prepare_powershell_arguments(config.InstallArguments),
                prepare_powershell_arguments(config.PackageParameters),
                config.ForceX86 ? " -forceX86" : string.Empty,
                config.OverrideArguments ? " -overrideArgs" : string.Empty
             );
        }

        private string prepare_powershell_arguments(string argument)
        {
            return argument.to_string().Replace("\"", "\\\"");
        }

        public bool run_action(ChocolateyConfiguration configuration, PackageResult packageResult, CommandNameType command)
        {
            var installerRun = false;

            var file = "chocolateyInstall.ps1";
            switch (command)
            {
                case CommandNameType.uninstall:
                    file = "chocolateyUninstall.ps1";
                    break;
            }

            var packageDirectory = packageResult.InstallLocation;
            if (packageDirectory.is_equal_to(ApplicationParameters.InstallLocation) || packageDirectory.is_equal_to(ApplicationParameters.PackagesLocation))
            {
                packageResult.Messages.Add(
                    new ResultMessage(
                        ResultType.Error,
                        "Install location is not specific enough, cannot run PowerShell script:{0} Erroneous install location captured as '{1}'".format_with(Environment.NewLine, packageResult.InstallLocation)
                        )
                    );

                return false;
            }

            if (!_fileSystem.directory_exists(packageDirectory))
            {
                packageResult.Messages.Add(new ResultMessage(ResultType.Error, "Package install not found:'{0}'".format_with(packageDirectory)));
                return installerRun;
            }

            var chocoPowerShellScript = get_script_for_action(packageResult, command);
            if (!string.IsNullOrEmpty(chocoPowerShellScript))
            {
                var failure = false;

                //todo: this is here for any possible compatibility issues. Should be reviewed and removed.
                ConfigurationBuilder.set_environment_variables(configuration);

                var package = packageResult.Package;
                Environment.SetEnvironmentVariable("chocolateyPackageName", package.Id);
                Environment.SetEnvironmentVariable("packageName", package.Id);
                Environment.SetEnvironmentVariable("chocolateyPackageVersion", package.Version.to_string());
                Environment.SetEnvironmentVariable("packageVersion", package.Version.to_string());
                Environment.SetEnvironmentVariable("chocolateyPackageFolder", packageDirectory);
                Environment.SetEnvironmentVariable("packageFolder", packageDirectory);
                Environment.SetEnvironmentVariable("installArguments", configuration.InstallArguments);
                Environment.SetEnvironmentVariable("installerArguments", configuration.InstallArguments);
                Environment.SetEnvironmentVariable("chocolateyInstallArguments", configuration.InstallArguments);
                Environment.SetEnvironmentVariable("packageParameters", configuration.PackageParameters);
                Environment.SetEnvironmentVariable("chocolateyPackageParameters", configuration.PackageParameters);
                if (configuration.ForceX86)
                {
                    Environment.SetEnvironmentVariable("chocolateyForceX86", "true");
                }
                if (configuration.OverrideArguments)
                {
                    Environment.SetEnvironmentVariable("chocolateyInstallOverride", "true");
                }
                
                if (configuration.NotSilent)
                {
                    Environment.SetEnvironmentVariable("chocolateyInstallOverride", "true");
                }
               
                //todo:if (configuration.NoOutput)
                //{
                //    Environment.SetEnvironmentVariable("ChocolateyEnvironmentQuiet","true");
                //}

                if (package.IsDownloadCacheAvailable)
                {
                    foreach (var downloadCache in package.DownloadCache.or_empty_list_if_null())
                    {
                        var urlKey = CryptoHashProvider.hash_value(downloadCache.OriginalUrl, CryptoHashProviderType.Sha256).Replace("=",string.Empty);
                        Environment.SetEnvironmentVariable("CacheFile_{0}".format_with(urlKey), downloadCache.FileName);
                        Environment.SetEnvironmentVariable("CacheChecksum_{0}".format_with(urlKey), downloadCache.Checksum);
                        Environment.SetEnvironmentVariable("CacheChecksumType_{0}".format_with(urlKey), "sha512");
                    }
                }

                this.Log().Debug(ChocolateyLoggers.Important, "Contents of '{0}':".format_with(chocoPowerShellScript));
                string chocoPowerShellScriptContents = _fileSystem.read_file(chocoPowerShellScript);
                this.Log().Debug(chocoPowerShellScriptContents.escape_curly_braces());

                bool shouldRun = !configuration.PromptForConfirmation;

                if (!shouldRun)
                {
                    this.Log().Info(ChocolateyLoggers.Important, () => "The package {0} wants to run '{1}'.".format_with(package.Id, _fileSystem.get_file_name(chocoPowerShellScript)));
                    this.Log().Info(ChocolateyLoggers.Important, () => "Note: If you don't run this script, the installation will fail.");
                    this.Log().Info(ChocolateyLoggers.Important, () => @"Note: To confirm automatically next time, use '-y' or consider setting 
 'allowGlobalConfirmation'. Run 'choco feature -h' for more details.");
                    
                    var selection = InteractivePrompt.prompt_for_confirmation(@"Do you want to run the script?", new[] {"yes", "no", "print"}, defaultChoice: null, requireAnswer: true);

                    if (selection.is_equal_to("print"))
                    {
                        this.Log().Info(ChocolateyLoggers.Important, "------ BEGIN SCRIPT ------");
                        this.Log().Info(() => "{0}{1}{0}".format_with(Environment.NewLine, chocoPowerShellScriptContents.escape_curly_braces()));
                        this.Log().Info(ChocolateyLoggers.Important, "------- END SCRIPT -------");
                        selection = InteractivePrompt.prompt_for_confirmation(@"Do you want to run this script?", new[] { "yes", "no" }, defaultChoice: null, requireAnswer: true);
                    }

                    if (selection.is_equal_to("yes")) shouldRun = true;
                    if (selection.is_equal_to("no"))
                    {
                        Environment.ExitCode = 1;
                        packageResult.Messages.Add(new ResultMessage(ResultType.Error, "User cancelled powershell portion of installation for '{0}'.{1} Specify -n to skip automated script actions.".format_with(chocoPowerShellScript, Environment.NewLine)));
                    }
                }

                if (shouldRun)
                {
                    installerRun = true;

                    if (configuration.Features.UsePowerShellHost)
                    {
                        add_assembly_resolver();
                    }

                    var result = new PowerShellExecutionResults
                    {
                        ExitCode = -1
                    };

                    try
                    {
                        result = configuration.Features.UsePowerShellHost
                                    ? Execute.with_timeout(configuration.CommandExecutionTimeoutSeconds).command(() => run_host(configuration, chocoPowerShellScript), result)
                                    : run_external_powershell(configuration, chocoPowerShellScript);
                    }
                    catch (Exception ex)
                    {
                        this.Log().Error(ex.Message.escape_curly_braces());
                        result.ExitCode = -1;
                    }

                    if (configuration.Features.UsePowerShellHost)
                    {
                        remove_assembly_resolver();
                    }

                    if (result.StandardErrorWritten && configuration.Features.FailOnStandardError)
                    {
                        failure = true;
                    }
                    else if (result.StandardErrorWritten && result.ExitCode == 0)
                    {
                        this.Log().Warn(
                            () =>
                            @"Only an exit code of non-zero will fail the package by default. Set 
 `--failonstderr` if you want error messages to also fail a script. See 
 `choco -h` for details.");
                    }

                    if (result.ExitCode != 0)
                    {
                        failure = true;
                    }

                    if (failure)
                    {
                        Environment.ExitCode = result.ExitCode;
                        packageResult.Messages.Add(new ResultMessage(ResultType.Error, "Error while running '{0}'.{1} See log for details.".format_with(chocoPowerShellScript, Environment.NewLine)));
                    }
                    packageResult.Messages.Add(new ResultMessage(ResultType.Note, "Ran '{0}'".format_with(chocoPowerShellScript)));
                }
            }

            return installerRun;
        }

        private class PowerShellExecutionResults
        {
            public int ExitCode { get; set; }
            public bool StandardErrorWritten { get; set; }
        }

        private PowerShellExecutionResults run_external_powershell(ChocolateyConfiguration configuration, string chocoPowerShellScript)
        {
            var result = new PowerShellExecutionResults();
            result.ExitCode = PowershellExecutor.execute(
                wrap_script_with_module(chocoPowerShellScript, configuration),
                _fileSystem,
                configuration.CommandExecutionTimeoutSeconds,
                (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    //inspect for different streams
                    if (e.Data.StartsWith("DEBUG:"))
                    {
                        this.Log().Debug(() => " " + e.Data.escape_curly_braces());
                    }
                    else if (e.Data.StartsWith("WARNING:"))
                    {
                        this.Log().Warn(() => " " + e.Data.escape_curly_braces());
                    }
                    else if (e.Data.StartsWith("VERBOSE:"))
                    {
                        this.Log().Info(ChocolateyLoggers.Verbose, () => " " + e.Data.escape_curly_braces());
                    }
                    else
                    {
                        this.Log().Info(() => " " + e.Data.escape_curly_braces());
                    }
                },
                (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    result.StandardErrorWritten = true;
                    this.Log().Error(() => " " + e.Data.escape_curly_braces());
                });

            return result;
        }

        private ResolveEventHandler _handler = null;

        private void add_assembly_resolver()
        {
            _handler = (sender, args) =>
            {
                var requestedAssembly = new AssemblyName(args.Name);

                this.Log().Debug(ChocolateyLoggers.Verbose, "Redirecting {0}, requested by '{1}'".format_with(args.Name, args.RequestingAssembly == null ? string.Empty : args.RequestingAssembly.FullName));

                AppDomain.CurrentDomain.AssemblyResolve -= _handler;

                // we build against v1 - everything should update in a kosher manner to the newest, but it may not.
                var assembly = attempt_version_load(requestedAssembly, new Version(5, 0, 0, 0)) ?? attempt_version_load(requestedAssembly, new Version(4, 0, 0, 0));
                if (assembly == null) assembly = attempt_version_load(requestedAssembly, new Version(3, 0, 0, 0));
                if (assembly == null) assembly = attempt_version_load(requestedAssembly, new Version(1, 0, 0, 0));

                return assembly;
            };

            AppDomain.CurrentDomain.AssemblyResolve += _handler;
        }

        private System.Reflection.Assembly attempt_version_load(AssemblyName requestedAssembly, Version version)
        {
            if (requestedAssembly == null) return null;

            requestedAssembly.Version = version;

            try
            {
                return System.Reflection.Assembly.Load(requestedAssembly);
            }
            catch (Exception ex)
            {
                this.Log().Debug(ChocolateyLoggers.Verbose, "Attempting to load assembly {0} failed:{1} {2}".format_with(requestedAssembly.Name, Environment.NewLine, ex.Message.escape_curly_braces()));
                return null;
            }
        }

        private void remove_assembly_resolver()
        {
            if (_handler != null)
            {
                AppDomain.CurrentDomain.AssemblyResolve -= _handler;
            }
        }

        private PowerShellExecutionResults run_host(ChocolateyConfiguration config, string chocoPowerShellScript)
        {
            // since we control output in the host, always set these true
            Environment.SetEnvironmentVariable("ChocolateyEnvironmentDebug", "true");
            Environment.SetEnvironmentVariable("ChocolateyEnvironmentVerbose", "true");
            
            var result = new PowerShellExecutionResults();
            string commandToRun = wrap_script_with_module(chocoPowerShellScript, config);
            var host = new PoshHost(config);
            this.Log().Debug(() => "Calling built-in PowerShell host with ['{0}']".format_with(commandToRun.escape_curly_braces()));
            
            var initialSessionState = InitialSessionState.CreateDefault();
            // override system execution policy without accidentally setting it
            initialSessionState.AuthorizationManager = new AuthorizationManager("choco");
            using (var runspace = RunspaceFactory.CreateRunspace(host, initialSessionState))
            {
                runspace.Open();

                // this will affect actual execution policy
                //RunspaceInvoke invoker = new RunspaceInvoke(runspace);
                //invoker.Invoke("Set-ExecutionPolicy ByPass");

                using (var pipeline = runspace.CreatePipeline())
                {
                    // The powershell host itself handles the following items:
                    // * Write-Debug
                    // * Write-Host
                    // * Write-Verbose
                    // * Write-Warning
                    //
                    // the two methods below will pick up Write-Output and Write-Error

                    // Write-Output
                    pipeline.Output.DataReady += (sender, args) =>
                    {
                        PipelineReader<PSObject> reader = sender as PipelineReader<PSObject>;

                        if (reader != null)
                        {
                            while (reader.Count > 0)
                            {
                                host.UI.WriteLine(reader.Read().to_string().escape_curly_braces());
                            }
                        }
                    };

                    // Write-Error
                    pipeline.Error.DataReady += (sender, args) =>
                    {
                        PipelineReader<object> reader = sender as PipelineReader<object>;

                        if (reader != null)
                        {
                            while (reader.Count > 0)
                            {
                                host.UI.WriteErrorLine(reader.Read().to_string().escape_curly_braces());
                            }
                        }
                    };

                    pipeline.Commands.Add(new Command(commandToRun, isScript: true, useLocalScope: false));

                    try
                    {
                        pipeline.Invoke();
                    }
                    catch (RuntimeException ex)
                    {
                        var errorStackTrace = ex.StackTrace;
                        var record = ex.ErrorRecord;
                        if (record != null)
                        {
                            // not available in v1
                            //errorStackTrace = record.ScriptStackTrace;
                            var scriptStackTrace = record.GetType().GetProperty("ScriptStackTrace");
                            if (scriptStackTrace != null)
                            {
                                var scriptError = scriptStackTrace.GetValue(record, null).to_string();
                                if (!string.IsNullOrWhiteSpace(scriptError)) errorStackTrace = scriptError;
                            }
                        }
                        this.Log().Error("ERROR: {0}{1}".format_with(ex.Message.escape_curly_braces(), !config.Debug ? string.Empty : "{0} {1}".format_with(Environment.NewLine, errorStackTrace.escape_curly_braces())));
                    }
                    catch (Exception ex)
                    {
                        // Unfortunately this doesn't print line number and character. It might be nice to get back to those items unless it involves tons of work.
                        this.Log().Error("ERROR: {0}{1}".format_with(ex.Message.escape_curly_braces(), !config.Debug ? string.Empty : "{0} {1}".format_with(Environment.NewLine, ex.StackTrace.escape_curly_braces())));
                    }

                    if (pipeline.PipelineStateInfo != null)
                    {
                        switch (pipeline.PipelineStateInfo.State)
                        {
                            // disconnected is not available unless the assembly version is at least v3
                            //case PipelineState.Disconnected:
                            case PipelineState.Running:
                            case PipelineState.NotStarted:
                            case PipelineState.Failed:
                            case PipelineState.Stopping:
                            case PipelineState.Stopped:
                                host.SetShouldExit(1);
                                host.HostException = pipeline.PipelineStateInfo.Reason;
                                break;
                            case PipelineState.Completed:
                                host.SetShouldExit(0);
                                break;
                        }

                    }
                }
            }

            this.Log().Debug("Built-in PowerShell host called with ['{0}'] exited with '{1}'.".format_with(commandToRun.escape_curly_braces(), host.ExitCode));

            result.ExitCode = host.ExitCode;
            result.StandardErrorWritten = host.StandardErrorWritten;

            return result;
        }
    }
}
