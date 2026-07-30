#include <everything.h>

class Program
{
    public: 
        static void Main(int argc, char *argv[]) {
            argparse::ArgumentParser program("ObjectRT");
            program.parse_args(argc,argv);
            
        } 
};


int main(int argc, char *argv[])
{
    Program::Main(argc,argv);
}