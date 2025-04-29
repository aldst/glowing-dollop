using System.Threading.Tasks;

namespace Quote.Contracts
{
    public interface IMarginProvider
    {
        decimal GetMargin(string code);
        Task<decimal> GetMarginByClient(string code);
    }
}
