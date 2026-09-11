using Etchv;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
public sealed class AssetsTests
{
 [Fact] public async Task AssetProtocol()
 {
  var record=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"assets.json")))!;string id=record["id"]!.GetValue<string>();
  var port=new TcpListener(IPAddress.Loopback,0);port.Start();int number=((IPEndPoint)port.LocalEndpoint).Port;port.Stop();
  using var listener=new HttpListener();string baseUrl=$"http://127.0.0.1:{number}";listener.Prefixes.Add(baseUrl+"/");listener.Start();
  var worker=Task.Run(async()=>{
   for(int i=0;i<7;i++){
    var context=await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));var request=context.Request;var response=context.Response;
    Assert.Equal("test-key",request.Headers["X-API-Key"]);int status=200;string output=record.ToJsonString();
    using var reader=new StreamReader(request.InputStream);string bodyText=await reader.ReadToEndAsync();
    if(request.QueryString["cursor"] is not null){status=409;output="{}";}
    else if(request.HttpMethod=="PATCH"){var body=JsonNode.Parse(bodyText)!;Assert.Equal(1,body["version"]!.GetValue<int>());Assert.Equal("renamed",body["name"]!.GetValue<string>());var updated=record.DeepClone();updated["version"]=2;output=updated.ToJsonString();}
    else if(request.HttpMethod=="DELETE"){status=204;}
    else if(request.HttpMethod=="POST"){Assert.Equal(id,JsonNode.Parse(bodyText)!["asset_ids"]![0]!.GetValue<string>());status=204;}
    else if(request.Url!.AbsolutePath.EndsWith("/content")){output="file";}
    else if(request.Url!.AbsolutePath=="/assets"){Assert.Equal("watermarked",request.QueryString["kind"]);output="{\"items\":["+record.ToJsonString()+"],\"next_cursor\":\"next-page\"}";}
    response.StatusCode=status;var bytes=Encoding.UTF8.GetBytes(output);response.ContentLength64=status==204?0:bytes.Length;if(status!=204)await response.OutputStream.WriteAsync(bytes);response.Close();
   }
  });
  try{
   using var c=new EtchvClient("test-key",baseUrl,TimeSpan.FromSeconds(2));
   Assert.Equal("next-page",(await c.ListAssetsAsync(new(Kind:"watermarked"))).NextCursor);
   Assert.Equal("launch",(await c.GetAssetAsync(id)).Metadata!.Value.GetProperty("campaign").GetString());
   Assert.Equal(2,(await c.UpdateAssetAsync(id,1,new Dictionary<string,object?>{{"name","renamed"}})).Version);
   Assert.Equal("file",Encoding.UTF8.GetString(await c.DownloadAssetAsync(id)));await c.DeleteAssetAsync(id);await c.DeleteAssetsAsync([id]);
   Assert.Equal(409,(await Assert.ThrowsAsync<EtchvException>(()=>c.ListAssetsAsync(new(Cursor:"next-page")))).StatusCode);
   await Assert.ThrowsAsync<ArgumentException>(()=>c.GetAssetAsync("../other"));await worker;
  }finally{listener.Stop();}
 }
}
