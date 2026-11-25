rm -rf ./bin/$2/
mkdir ./bin/$2/ -p
cp $1 ./bin/$2/
cp $3/Archipelago.MultiClient.Net.dll ./bin/$2/
cp $3/Newtonsoft.Json.dll ./bin/$2/
cp ./Mod/* --target-directory ./bin/$2/