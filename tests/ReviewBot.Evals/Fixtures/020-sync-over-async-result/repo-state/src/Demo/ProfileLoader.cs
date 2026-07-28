using System.Net.Http;
using System.Threading.Tasks;

namespace Demo;

public sealed class ProfileLoader
{
    private readonly HttpClient httpClient;

    public ProfileLoader(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public string LoadProfileJson(string url)
    {
        return this.httpClient.GetStringAsync(url).Result;
    }
}
