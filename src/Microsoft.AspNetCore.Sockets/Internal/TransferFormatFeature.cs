using Microsoft.AspNetCore.Sockets.Features;

namespace Microsoft.AspNetCore.Sockets.Internal
{
    public class TransferFormatFeature : ITransferFormatFeature
    {
        public TransferFormat SupportedFormats { get; }
        public TransferFormat ActiveFormat { get; set; }

        public TransferFormatFeature(TransferFormat supportedFormats)
        {
            SupportedFormats = supportedFormats;
        }
    }
}
