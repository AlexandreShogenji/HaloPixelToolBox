using HaloPixelToolBox;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services.Audio;
using HaloPixelToolBox.ViewModels;
int passed=0;
void Check(bool condition,string name){if(!condition)throw new Exception(name);passed++;}
(AudioControlService service,AudioControlPageViewModel vm) New(){App.AudioControl=new();var vm=new AudioControlPageViewModel();vm.Attach();return(App.AudioControl,vm);}
{
 var(s,v)=New(); Check(v.Bands.Count==10,"ten bands");Check(v.SelectedEndpoint?.Id=="speaker","target restored");Check(!v.Enabled,"effects default off");Check(s.UpdateCalls==0,"refresh never rewrites profile");
 v.Bands[0].GainDb=1;v.Bands[0].GainDb=2;v.Bands[0].GainDb=3;await Task.Delay(350);Check(s.UpdateCalls==1,"debounce collapsed edits");Check(s.LastUpdate!.BandGainsDb[0]==3,"latest gain");Check(!v.EffectStatus.Contains("正在保存"),"pending cleared");v.Detach();
}
{
 var(s,v)=New();v.Bands[1].GainDb=4;s.Push(s.Current.Profile with{PreampDb=-6});Check(v.Bands[1].GainDb==4&&v.PreampDb==0,"external snapshot preserves draft");v.Detach();await Task.Delay(30);Check(s.LastUpdate!.BandGainsDb[1]==4,"detach flushes draft");v.Attach();Check(v.Bands[1].GainDb==4,"reattach restores latest");v.Detach();
}
{
 var(s,v)=New();v.Bands[2].GainDb=5;v.PresetName="Saved";await v.SavePresetCommand.ExecuteAsync(null);Check(s.SaveCalls==1,"preset stored");Check(s.LastPreset!.BandGainsDb[2]==5,"preset flush latest draft");Check(v.SelectedPreset?.Name=="Saved","saved preset selected");Check(s.UpdateCalls==1,"save flush avoids debounce duplicate");v.Detach();
}
{
 var(s,v)=New();s.FailNextUpdate=true;v.Bands[3].GainDb=5;await v.SavePresetCommand.ExecuteAsync(null);Check(s.SaveCalls==0,"failed draft does not save wrong curve");v.Detach();
}
{
 var(s,v)=New();Check(!v.CanDeletePreset,"flat protected");await v.DeletePresetCommand.ExecuteAsync(null);Check(s.Current.Presets.Count==1,"flat remains");v.PreampDb=-30;v.Balance=125;v.Bands[9].GainDb=double.NaN;Check(v.PreampDb==-30,"full valid preamp range");Check(v.Balance==100,"balance clamp");Check(v.Bands[9].GainDb==0,"NaN normalized");v.Detach();
}
{
 var(s,v)=New();s.Push(s.Current.Profile with{PreampDb=-5},50);Check(v.PreampDb==-5,"new revision accepted");s.Push(s.Current.Profile with{PreampDb=5},1);Check(v.PreampDb==-5,"old revision rejected");v.Detach();
}
{
 var(s,v)=New();v.Bands[0].GainDb=1;v.Detach();int calls=s.UpdateCalls;await Task.Delay(350);Check(s.UpdateCalls==calls,"detached debounce canceled");
}
{
 var(s,v)=New();int notifications=0;v.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(v.EffectivePreampLabel))notifications++;};v.PreampDb=3;v.Bands[0].GainDb=4;Check(v.EffectivePreampLabel.Contains("-4.0"),"auto headroom effective preamp");v.AutoHeadroom=false;Check(v.EffectivePreampLabel.Contains("+3.0"),"manual effective preamp");Check(notifications>=3,"effective preamp changes notify");v.Detach();
}
{
 var(s,v)=New();
 s.PushEnvironment(endpoints:[new("speaker","Halo",true,false),new("communications","FxSound Speakers",false,true)]);
 Check(v.EndpointStatus.Contains("Windows 播放输出：Halo"),"media output remains explicit");
 Check(v.EndpointStatus.Contains("通话输出：FxSound Speakers"),"different communications output is shown");
 v.SelectedEndpoint=v.Endpoints[1];
 await Task.Delay(350);
 Check(s.OutputChangeCalls==0,"target dropdown never changes output implicitly");
 Check(s.LastUpdate!.EndpointId=="communications","dropdown saves curve target");
 await v.SetDefaultOutputCommand.ExecuteAsync(null);
 Check(s.OutputChangeCalls==1,"explicit button changes output");
 Check(!v.EndpointStatus.Contains("与播放输出不同"),"same output roles no longer show mismatch");
 v.Detach();
}
{
 var(s,v)=New();
 s.PushEnvironment(backend:s.Current.Backend with{ConfigurationWritten=false});
 Check(v.EffectStatus.Contains("音效已关闭"),"disabled effects override unconfirmed write status");
 Check(v.BackendTitle.Contains("音效关闭"),"ready backend does not imply effects enabled");
 s.Push(s.Current.Profile with{Enabled=true});
 Check(v.EffectStatus.Contains("尚未确认写入"),"enabled settings do not imply confirmed write");
 s.PushEnvironment(backend:s.Current.Backend with{ConfigurationWritten=true});
 Check(v.EffectStatus.Contains("播放音频可验证效果"),"written curve retains audio verification boundary");
 s.PushEnvironment(backend:s.Current.Backend with{CanApply=false});
 Check(v.EffectStatus.Contains("配置完成后才能生效"),"unavailable backend does not imply effects active");
 s.Push(s.Current.Profile with{Enabled=false});
 Check(v.EffectStatus.Contains("音效已关闭"),"disabled effects remain clear with unavailable backend");
 v.Detach();
}
Console.WriteLine($"PASS {passed} UI state checks; no hardware calls.");
