wget https://github.com/protocolbuffers/protobuf/releases/download/v3.7.0/protoc-3.7.0-win64.zip -OutFile C:\protoc-3.7.0-win64.zip; 
Expand-Archive -Path C:\protoc-3.7.0-win64.zip -DestinationPath C:\protoc ;
cp -r C:\protoc\include\ C:\WINDOWS\; 
cp C:\protoc\bin\protoc.exe  C:\WINDOWS\;
protoc --version;
