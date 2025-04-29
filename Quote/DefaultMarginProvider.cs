using System;
using Newtonsoft.Json;
using System.Threading.Tasks;
using Quote.Contracts;
using System.Net.Http;

namespace Quote
{
    public class DefaultMarginProvider : IMarginProvider
    {
        public decimal GetMargin(string code)
        {
            var margin = GetMarginByClient(code);
            if(margin.Result != 0.0M)
                return margin.Result;

            return 0.25M;
        }

        public async Task<decimal> GetMarginByClient(string code)
        {
            string apiUrl = $"http://refactored-pancake.free.beeceptor.com/margin/{code}";
            decimal margin = 0.0M;

            if (string.IsNullOrEmpty(code))
                return margin;

            using (var client = new HttpClient())
            {
                try
                {
                    HttpResponseMessage response = await client.GetAsync(apiUrl);

                    if (response.IsSuccessStatusCode)
                    {
                        string contenido = await response.Content.ReadAsStringAsync();
                        var jsonResponse = JsonConvert.DeserializeObject<dynamic>(contenido);
                        return (decimal)jsonResponse.margin;
                    }
                    return margin;
                }
                catch (HttpRequestException ex)
                {
                    return margin;
                }
            }
        }
    }
}
