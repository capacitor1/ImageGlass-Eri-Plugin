namespace EriCodec;

public class Misc
{
    internal abstract class ERISADecodeContext
    {
        protected int       m_nIntBufCount;
        protected uint      m_dwIntBuffer;
        protected uint      m_nBufferingSize;
        protected uint      m_nBufCount;
        protected byte[]    m_ptrBuffer;
        protected int       m_ptrNextBuf;

        protected Stream    m_pFile;
        protected ERISADecodeContext m_pContext;

        public ERISADecodeContext (uint nBufferingSize)
        {
            m_nIntBufCount = 0;
            m_nBufferingSize = (nBufferingSize + 0x03) & ~0x03u;
            m_nBufCount = 0;
            m_ptrBuffer = new byte[nBufferingSize];
            m_pFile = null;
            m_pContext = null;
        }

        public void AttachInputFile (Stream file)
        {
            m_pFile = file;
            m_pContext = null;
        }

        public void AttachInputContext (ERISADecodeContext context)
        {
            m_pFile = null;
            m_pContext = context;
        }

        public uint ReadNextData (byte[] ptrBuffer, uint nBytes)
        {
            if (m_pFile != null)
            {
                return (uint)m_pFile.Read (ptrBuffer, 0, (int)nBytes);
            }
            else if (m_pContext != null)
            {
                return m_pContext.DecodeBytes (ptrBuffer, nBytes);
            }
            else
            {
                throw new ApplicationException ("Uninitialized ERISA encryption context");
            }
        }

        public abstract uint DecodeBytes (Array ptrDst, uint nCount);

        protected bool PrefetchBuffer()
        {
            if (0 == m_nIntBufCount)
            {
                if (0 == m_nBufCount)
                {
                    m_ptrNextBuf = 0; // m_ptrBuffer;
                    m_nBufCount = ReadNextData (m_ptrBuffer, m_nBufferingSize);
                    if (0 == m_nBufCount)
                    {
                        return false;
                    }
                    if (0 != (m_nBufCount & 0x03))
                    {
                        uint    i = m_nBufCount;
                        m_nBufCount += 4 - (m_nBufCount & 0x03);
                        while (i < m_nBufCount)
                            m_ptrBuffer[i ++] = 0;
                    }
                }
                m_nIntBufCount = 32;
                m_dwIntBuffer =
                      ((uint)m_ptrBuffer[m_ptrNextBuf] << 24) | ((uint)m_ptrBuffer[m_ptrNextBuf+1] << 16)
                    | ((uint)m_ptrBuffer[m_ptrNextBuf+2] << 8) | (uint)m_ptrBuffer[m_ptrNextBuf+3];
                m_ptrNextBuf += 4;
                m_nBufCount -= 4;
            }
            return true;
        }

        public void FlushBuffer ()
        {
            m_nIntBufCount = 0;
            m_nBufCount = 0;
        }

        public int GetABit ()
        {
            if (!PrefetchBuffer())
            {
                return  1;
            }
            int nValue = ((int)m_dwIntBuffer) >> 31;
            --m_nIntBufCount;
            m_dwIntBuffer <<= 1;
            return nValue;
        }

        public uint GetNBits (int n)
        {
            uint nCode = 0;
            while (n != 0)
            {
                if (!PrefetchBuffer())
                    break;

                int nCopyBits = Math.Min (n, m_nIntBufCount);
                nCode = (nCode << nCopyBits) | (m_dwIntBuffer >> (32 - nCopyBits));
                n -= nCopyBits;
                m_nIntBufCount -= nCopyBits;
                m_dwIntBuffer <<= nCopyBits;
            }
            return nCode;
        }
    }
}