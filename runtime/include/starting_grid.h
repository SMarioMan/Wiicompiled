#pragma once

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <stdexcept>
#include <vector>

namespace StartingGrid {
using Vec = std::array<float, 3>;
inline Vec Sub(Vec a, const Vec& b) { for (unsigned i=0;i<3;i++) a[i]-=b[i]; return a; }
inline float Dot(const Vec& a, const Vec& b) { return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]; }
inline Vec Unit(Vec a) { const auto length=std::sqrt(Dot(a,a)); if(length<.001f) throw std::runtime_error("Degenerate authored starting grid"); for(auto& x:a)x/=length; return a; }
struct Point { float u, v; };
inline float Cross(Point a, Point b, Point c) { return (b.u-a.u)*(c.v-a.v)-(b.v-a.v)*(c.u-a.u); }

// Pack the expanded field inside the course's authored twelve-racer footprint.
// Work in the actual transformed grid plane, including pitch and roll. No row
// may continue indefinitely behind the start, and no new world-space offsets
// or per-course coordinates are needed.
inline std::vector<Vec> Pack(const std::array<Vec,12>& authored, unsigned count) {
    if(!count) return {};
    const auto u=Unit(Sub(authored[1],authored[0]));
    auto v=Sub(authored[3],authored[0]); const float along=Dot(v,u);
    for(unsigned i=0;i<3;i++) v[i]-=along*u[i]; v=Unit(v);
    std::vector<Point> points;
    for(const auto& p:authored) { const auto delta=Sub(p,authored[0]); points.push_back({Dot(delta,u),Dot(delta,v)}); }
    std::sort(points.begin(),points.end(),[](Point a,Point b){return a.u<b.u || (a.u==b.u && a.v<b.v);});
    std::vector<Point> hull;
    for(const auto& p:points) { while(hull.size()>1 && Cross(hull[hull.size()-2],hull.back(),p)<=0)hull.pop_back(); hull.push_back(p); }
    const auto lower=hull.size();
    for(auto i=points.rbegin()+1;i!=points.rend();++i) { while(hull.size()>lower && Cross(hull[hull.size()-2],hull.back(),*i)<=0)hull.pop_back(); hull.push_back(*i); }
    hull.pop_back();
    if(hull.size()<3)throw std::runtime_error("Authored starting grid has no area");
    float front=std::numeric_limits<float>::max(),back=-front;
    for(auto p:hull) { front=std::min(front,p.v); back=std::max(back,p.v); }
    auto interval=[&](float row) {
        float left=std::numeric_limits<float>::max(),right=-left;
        for(unsigned i=0;i<hull.size();i++) {
            const auto a=hull[i],b=hull[(i+1)%hull.size()];
            if(row<std::min(a.v,b.v)-.001f || row>std::max(a.v,b.v)+.001f)continue;
            if(std::abs(a.v-b.v)<.001f) { left=std::min({left,a.u,b.u});right=std::max({right,a.u,b.u}); }
            else { const float x=a.u+(b.u-a.u)*std::clamp((row-a.v)/(b.v-a.v),0.f,1.f);left=std::min(left,x);right=std::max(right,x); }
        }
        if(left>right)throw std::runtime_error("Starting grid row misses its authored footprint");
        return std::array<float,2>{left,right};
    };
    std::vector<Point> best; float bestSeparation=-1;
    // Balanced rows avoid an isolated final racer. Choose the lattice with the
    // greatest minimum pair distance, rather than fixing twelve or three columns.
    for(unsigned rows=1;rows<=count;rows++) {
        std::vector<Point> trial;
        for(unsigned row=0;row<rows;row++) {
            const unsigned columns=count/rows+(row<count%rows);
            const float depth=rows==1 ? (front+back)*.5f : front+(back-front)*row/(rows-1);
            const auto limits=interval(depth);
            for(unsigned col=0;col<columns;col++)trial.push_back({columns==1 ? (limits[0]+limits[1])*.5f : limits[0]+(limits[1]-limits[0])*col/(columns-1),depth});
        }
        float separation=std::numeric_limits<float>::max();
        for(unsigned i=0;i<trial.size();i++)for(unsigned j=0;j<i;j++) {
            const float x=trial[i].u-trial[j].u,z=trial[i].v-trial[j].v;
            separation=std::min(separation,x*x+z*z);
        }
        if(separation>bestSeparation) { bestSeparation=separation;best=std::move(trial); }
    }
    std::vector<Vec> result;
    for(auto p:best) { auto position=authored[0];for(unsigned i=0;i<3;i++)position[i]+=u[i]*p.u+v[i]*p.v;result.push_back(position); }
    return result;
}
} // namespace StartingGrid
