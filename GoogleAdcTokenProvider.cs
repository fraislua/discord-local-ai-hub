using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;

namespace DiscordAIBot
{
    // ADC(Application Default Credentials)のJSONファイルからGoogleCredentialを読み込み、
    // Vertex AI呼び出し用のアクセストークンを提供する。
    // VertexGeminiProvider / VertexGrokProvider から共有インスタンスとして使い回す想定。
    public class GoogleAdcTokenProvider
    {
        private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

        private readonly GoogleCredential _credential;

        public GoogleAdcTokenProvider(string credentialPath)
        {
            // FromFile(string)はGoogle.Apis.Auth 1.76.0で非推奨化されているが、
            // 動的に型判定される任意ファイルを読む場合の注意喚起であり、
            // 自前で配置した既知のADC JSONを読むだけの本用途では問題ない。
#pragma warning disable CS0618
            _credential = GoogleCredential.FromFile(credentialPath);
#pragma warning restore CS0618

            if (_credential.IsCreateScopedRequired)
            {
                _credential = _credential.CreateScoped(CloudPlatformScope);
            }
        }

        // トークンのキャッシュ・期限切れ時の自動リフレッシュはGoogleCredential内部(ITokenAccess実装)に任せる
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            return ((ITokenAccess)_credential).GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        }
    }
}
