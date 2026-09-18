/* ========================================================================
 * Copyright (c) 2005-2021 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Foundation MIT License 1.00
 * 
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 * 
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/MIT/1.00/
 * ======================================================================*/




using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Opc.Ua.Utils;

namespace Opc2Aml
{

    public class Configuration
    {
        public void Load( IConfiguration configuration )
        {
            try
            {
                IConfigurationSection trace = configuration.GetSection( "TraceConfiguration" );
                if( trace != null )
                {
                    string outputFilePath = trace.GetValue<string>( "OutputFilePath" );
                    if( outputFilePath != null )
                    {
                        try
                        {
                            FileInfo outputFile = new FileInfo( outputFilePath );
                            _outputFilePath = outputFilePath;
                        }
                        catch( Exception ex )
                        {
                            // Unable to parse fileName
                        }
                    }

                    _deleteOnLoad = trace.GetValue<bool>( "DeleteOnLoad" );

                    string logLevel = trace.GetValue<string>( "LogLevel" );
                    if( logLevel != null )
                    {
                        LogLevel level;
                        if( Enum.TryParse( logLevel, ignoreCase: true, out level ) )
                        {
                            _level = level;
                        }
                    }
                }
            }
            catch ( Exception )
            {
                // There is no logging.
            }
        }

        private Opc.Ua.TraceConfiguration GetTraceConfiguration()
        {
            if ( _trace == null )
            {
                _trace = new Opc.Ua.TraceConfiguration();
                _trace.TraceMasks = TraceMasks.None;

                if ( OutputFilePath != string.Empty )
                {
                    FileInfo outputFile = new FileInfo( OutputFilePath );
                    _trace.OutputFilePath = outputFile.FullName;
                    _trace.DeleteOnLoad = DeleteOnLoad;
                    
                    switch( _level )
                    {
                        case LogLevel.Trace:
                        case LogLevel.Debug:
                            _trace.TraceMasks = 
                                TraceMasks.Error | 
                                TraceMasks.Information |
                                TraceMasks.StackTrace | 
                                TraceMasks.Service |
                                TraceMasks.ServiceDetail |
                                TraceMasks.Operation | 
                                TraceMasks.OperationDetail | 
                                TraceMasks.StartStop | 
                                TraceMasks.ExternalSystem | 
                                TraceMasks.Security;
                            break;

                        case LogLevel.Information:
                            _trace.TraceMasks = TraceMasks.Error | TraceMasks.Information;
                            break;

                        case LogLevel.Warning:
                        case LogLevel.Error:
                        case LogLevel.Critical:
                            _trace.TraceMasks = TraceMasks.Error;
                            break;

                        case LogLevel.None:
                        default:
                            // Mask already set to none
                            break;
                    }
                }
            }

            return _trace;
        }

        #region Properties

        public string OutputFilePath { get { return _outputFilePath; } }

        public bool DeleteOnLoad { get { return _deleteOnLoad; } }

        public LogLevel Level { get { return _level; } }

        public Opc.Ua.TraceConfiguration TraceConfiguration { get { return GetTraceConfiguration(); } }

        #endregion

        #region Variables

        private string _outputFilePath = string.Empty;
        private bool _deleteOnLoad = true;
        private LogLevel _level = LogLevel.None;
        Opc.Ua.TraceConfiguration _trace = null;

        #endregion

    }
}
