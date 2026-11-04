#include "hle/socket_option_contract.h"
#include <array>
#include <cstdlib>
#include <cstring>
#include <iostream>
#if !defined(_WIN32)
#include <unistd.h>
#endif

#if defined(_WIN32)
using Socket = SOCKET;
using Length = int;
constexpr Socket invalidSocket = INVALID_SOCKET;
void Close(Socket socket) { closesocket(socket); }
#else
using Socket = int;
using Length = socklen_t;
constexpr Socket invalidSocket = -1;
void Close(Socket socket) { close(socket); }
#endif
void Check(bool value, const char* message) {
    if (!value) { std::cerr << message << '\n'; std::exit(1); }
}
void Set(Socket socket, uint32_t level, uint32_t name, std::span<const uint8_t> bytes) {
    int integer = 0;
    const void* value = bytes.data();
    int length = static_cast<int>(bytes.size());
    if (SocketOptionContract::IsInteger(level, name)) {
        Check(SocketOptionContract::Integer(bytes, integer), "Invalid guest integer option");
        value = &integer; length = sizeof(integer);
    }
    Check(setsockopt(socket, SocketOptionContract::Level(level), SocketOptionContract::Name(level, name),
        static_cast<const char*>(value), length) == 0, "Host rejected translated guest socket option");
}
int main() {
#if defined(_WIN32)
    WSADATA data{};
    Check(WSAStartup(MAKEWORD(2,2), &data) == 0, "WSAStartup failed");
#endif
    const std::array<uint8_t,4> enabled{0,0,0,1};
    const std::array<uint8_t,4> interfaceAddress{127,0,0,1};
    const std::array<uint8_t,8> membership{239,255,77,75,127,0,0,1};
    std::array<Socket,2> receivers{::socket(AF_INET,SOCK_DGRAM,0), ::socket(AF_INET,SOCK_DGRAM,0)};
    sockaddr_in address{};address.sin_family=AF_INET;address.sin_addr.s_addr=htonl(INADDR_ANY);
    for (unsigned i=0;i<receivers.size();i++) {
        Check(receivers[i]!=invalidSocket, "Cannot create multicast receiver");
        Set(receivers[i],0xffff,4,enabled);
        Check(bind(receivers[i],reinterpret_cast<sockaddr*>(&address),sizeof(address))==0, "Cannot share multicast port");
        if (!i) { Length size=sizeof(address);Check(getsockname(receivers[i],reinterpret_cast<sockaddr*>(&address),&size)==0,"getsockname failed"); }
        Set(receivers[i],0,12,membership);
    }
    Socket sender=::socket(AF_INET,SOCK_DGRAM,0);
    Check(sender!=invalidSocket,"Cannot create sender");
    sockaddr_in source{};source.sin_family=AF_INET;source.sin_addr.s_addr=htonl(0x7f000002);
    Check(bind(sender,reinterpret_cast<sockaddr*>(&source),sizeof(source))==0,"Cannot bind per-instance source identity");
    Set(sender,0,9,interfaceAddress);Set(sender,0,10,enabled);Set(sender,0,11,enabled);
    address.sin_addr.s_addr=htonl(0xefff4d4b);
    constexpr char payload[]="WiiCompiled multicast discovery";
    Check(sendto(sender,payload,sizeof(payload),0,reinterpret_cast<sockaddr*>(&address),sizeof(address))==sizeof(payload),"Multicast send failed");
    for (Socket receiver:receivers) {
        fd_set read;FD_ZERO(&read);FD_SET(receiver,&read);timeval timeout{2,0};
        Check(select(static_cast<int>(receiver)+1,&read,nullptr,nullptr,&timeout)==1,"A multicast subscriber missed discovery");
        char buffer[128]{};sockaddr_in peer{};Length size=sizeof(peer);
        Check(recvfrom(receiver,buffer,sizeof(buffer),0,reinterpret_cast<sockaddr*>(&peer),&size)==sizeof(payload),"Unexpected multicast packet size");
        Check(std::memcmp(buffer,payload,sizeof(payload))==0,"Multicast content changed");
        Check(ntohl(peer.sin_addr.s_addr)==0x7f000002,"Multicast lost per-instance source identity");
        Set(receiver,0,13,membership);Close(receiver);
    }
    Close(sender);
#if defined(_WIN32)
    WSACleanup();
#endif
}
