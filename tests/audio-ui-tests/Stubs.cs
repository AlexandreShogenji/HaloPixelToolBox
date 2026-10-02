global using Microsoft.UI.Xaml;
global using Microsoft.UI.Xaml.Controls;
using HaloPixelToolBox.Models;
namespace Microsoft.UI.Xaml { public enum Visibility { Visible,Collapsed } }
namespace Microsoft.UI.Xaml.Controls { public enum InfoBarSeverity { Informational,Warning,Error,Success } }
namespace Microsoft.UI.Dispatching { public class DispatcherQueue { public static DispatcherQueue? GetForCurrentThread()=>null; public bool HasThreadAccess=>true; public bool TryEnqueue(Action action){action();return true;} } }
namespace HaloPixelToolBox { public class App { public static Services.Audio.AudioControlService AudioControl {get;set;}=new(); } }
namespace HaloPixelToolBox.Services.Audio {
 public sealed record AudioControlSnapshot(AudioControlProfile Profile,IReadOnlyList<AudioControlPreset> Presets,IReadOnlyList<AudioEndpointInfo> Endpoints,AudioBackendStatus Backend,string Message,long Revision,bool OperationSucceeded);
 public class AudioControlService {
   public AudioControlSnapshot Current {get;private set;}=new(new(){EndpointId="speaker"},[new(){Id="flat",Name="平直原声"}],[new("speaker","Halo",true)],new(){CanApply=true,ConfigurationWritten=true,IsInstalled=true},"Ready",0,true);
   public event Action<AudioControlSnapshot>? Changed;
   public int UpdateCalls,SaveCalls,OutputChangeCalls; public bool FailNextUpdate; public AudioControlProfile? LastUpdate,LastPreset;
   public Func<Task>? BeforeUpdate;
   public void Push(AudioControlProfile profile,long? revision=null) {Current=Current with{Profile=profile.Copy(),Revision=revision??Current.Revision+1};Changed?.Invoke(Current);}
   public void PushEnvironment(AudioBackendStatus? backend=null,IReadOnlyList<AudioEndpointInfo>? endpoints=null){Current=Current with{Backend=backend??Current.Backend,Endpoints=endpoints??Current.Endpoints,Revision=Current.Revision+1};Changed?.Invoke(Current);}
   public Task<AudioControlSnapshot> RefreshAsync(CancellationToken ct=default)=>Task.FromResult(Current);
   public async Task<AudioControlSnapshot> UpdateProfileAsync(AudioControlProfile profile,CancellationToken ct=default){UpdateCalls++;LastUpdate=profile.Copy();if(BeforeUpdate is not null)await BeforeUpdate(); if(FailNextUpdate){FailNextUpdate=false;Current=Current with{Message="Failed",OperationSucceeded=false,Revision=Current.Revision+1};Changed?.Invoke(Current);return Current;}Push(profile);return Current;}
   public Task<AudioControlSnapshot> InitializeBackendAsync(CancellationToken ct=default)=>Task.FromResult(Current);
   public Task<AudioControlSnapshot> SetDefaultOutputAsync(string id,CancellationToken ct=default){OutputChangeCalls++;PushEnvironment(endpoints:Current.Endpoints.Select(endpoint=>endpoint with{IsDefault=endpoint.Id==id,IsDefaultCommunications=endpoint.Id==id}).ToArray());return Task.FromResult(Current);}
   public Task<AudioControlSnapshot> ApplyPresetAsync(string id,CancellationToken ct=default){Push(Current.Profile with{BandGainsDb=new double[10]});return Task.FromResult(Current);}
   public Task<AudioControlSnapshot> SavePresetAsync(string name,CancellationToken ct=default){SaveCalls++;LastPreset=Current.Profile.Copy();Current=Current with{Presets=Current.Presets.Append(new AudioControlPreset(){Name=name,Profile=LastPreset}).ToArray(),Revision=Current.Revision+1};Changed?.Invoke(Current);return Task.FromResult(Current);}
   public Task<AudioControlSnapshot> DeletePresetAsync(string id,CancellationToken ct=default)=>Task.FromResult(Current);
   public void OpenConfigurator(){} public static void OpenOfficialDownload(){} public static void OpenSoundSettings(){}
 }
}
